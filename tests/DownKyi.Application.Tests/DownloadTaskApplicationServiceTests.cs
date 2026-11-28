using DownKyi.Application.Downloads;
using DownKyi.Application.Settings;
using DownKyi.Application.Time;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;

namespace DownKyi.Application.Tests;

public sealed class DownloadTaskApplicationServiceTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 7, 22, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RepeatedCancelAndLateProgressDoNotWriteOrPublish()
    {
        var store = new RecordingStore();
        using var service = new DownloadTaskApplicationService(store,
            DownloadHistoryService.CreateForSharedStore(store), new AdvancingClock());
        var task = CreateTask().Cancel(Epoch.AddSeconds(1)).RequireValue();
        await service.AddAsync(task, TestContext.Current.CancellationToken);
        var events = 0;
        service.TaskChanged += (_, _) => events++;

        var canceled = await service.CancelAsync(task.Id, TestContext.Current.CancellationToken);
        var progress = await service.UpdateProgressAsync(task.Id,
            new DownloadProgress(50, 50, 100), TestContext.Current.CancellationToken);

        Assert.Same(task, canceled.RequireValue());
        Assert.Same(task, progress.RequireValue());
        Assert.Empty(store.ExpectedVersions);
        Assert.Equal(0, events);
        Assert.Same(task, store.Current);
    }

    [Fact]
    public async Task ResumingQueuedTaskDoesNotWriteOrPublish()
    {
        var store = new RecordingStore();
        using var service = new DownloadTaskApplicationService(store, DownloadHistoryService.CreateForSharedStore(store), new AdvancingClock());
        var task = CreateTask();
        await service.AddAsync(task, TestContext.Current.CancellationToken);
        var eventCount = 0;
        service.TaskChanged += (_, _) => eventCount++;

        var result = await service.ResumeAsync(task.Id, TestContext.Current.CancellationToken);

        Assert.Same(task, result.RequireValue());
        Assert.Same(task, store.Current);
        Assert.Empty(store.ExpectedVersions);
        Assert.Equal(0, eventCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyResumeAndRetryAreRejectedBeforeWritingOrPublishing(bool retry)
    {
        var store = new RecordingStore();
        using var service = new DownloadTaskApplicationService(store, DownloadHistoryService.CreateForSharedStore(store), new AdvancingClock());
        var task = CreateTask()
            .UpdateTransferState(DownloadTransferState.LegacyEmpty, Epoch.AddSeconds(1)).RequireValue()
            .Fail(new DownloadFailure("legacy.removed", "Legacy transfer removed.", true), Epoch.AddSeconds(2)).RequireValue();
        await service.AddAsync(task, TestContext.Current.CancellationToken);
        var eventCount = 0;
        service.TaskChanged += (_, _) => eventCount++;

        var result = retry
            ? await service.RetryAsync(task.Id, TestContext.Current.CancellationToken)
            : await service.ResumeAsync(task.Id, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.resume.legacy-transfer", result.Error?.Code);
        Assert.Same(task, store.Current);
        Assert.Empty(store.ExpectedVersions);
        Assert.Equal(0, eventCount);
    }

    [Fact]
    public async Task ResumeRevalidatesLegacyProvenanceAfterStoreConflict()
    {
        var store = new RecordingStore();
        using var service = new DownloadTaskApplicationService(store, DownloadHistoryService.CreateForSharedStore(store), new AdvancingClock());
        var paused = CreateTask().Pause(Epoch.AddSeconds(1)).RequireValue();
        await service.AddAsync(paused, TestContext.Current.CancellationToken);
        var concurrent = paused.UpdateTransferState(
            DownloadTransferState.LegacyEmpty,
            Epoch.AddSeconds(2)).RequireValue();
        store.ConcurrentUpdate = concurrent;
        var eventCount = 0;
        service.TaskChanged += (_, _) => eventCount++;

        var result = await service.ResumeAsync(paused.Id, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.resume.legacy-transfer", result.Error?.Code);
        Assert.Same(concurrent, store.Current);
        Assert.Equal(paused.Version, Assert.Single(store.ExpectedVersions));
        Assert.Equal(0, eventCount);
    }

    [Fact]
    public async Task CommandsPersistAggregateBeforePublishingProjectionEvent()
    {
        var store = new RecordingStore();
        using var service = new DownloadTaskApplicationService(store, DownloadHistoryService.CreateForSharedStore(store), new AdvancingClock());
        var publishedVersions = new List<long>();
        service.TaskChanged += (_, args) =>
        {
            if (args.Snapshot != null)
            {
                Assert.Same(args.Snapshot, store.Current);
                publishedVersions.Add(args.Snapshot.Version);
            }
        };

        var task = CreateTask();
        Assert.True((await service.AddAsync(task, TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await service.StartAsync(task.Id, TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await service.RecordTransferFileAsync(
            task.Id,
            "video-1",
            "segment.m4s",
            TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await service.SetBackendIdentityAsync(
            task.Id,
            "aria-gid",
            TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await service.CompleteTransferFileAsync(
            task.Id,
            "video-1",
            TestContext.Current.CancellationToken)).IsSuccess);

        var stored = Assert.IsType<DownloadTask>(store.Current);
        Assert.Equal(DownloadPhase.Downloading, stored.Phase);
        Assert.Equal("segment.m4s", stored.Plan.TransferFiles["video-1"]);
        Assert.Equal(task.Plan.NfoRequest, stored.Plan.NfoRequest);
        Assert.Null(stored.Transfer.BackendIdentity);
        Assert.Equal("video-1", Assert.Single(stored.Transfer.CompletedFileKeys));
        Assert.Equal([0L, 1L, 2L, 3L, 4L], publishedVersions);
    }

    [Fact]
    public async Task ShutdownRecoveryPreservesResumeStateAndOptimisticVersion()
    {
        var store = new RecordingStore();
        using var service = new DownloadTaskApplicationService(store, DownloadHistoryService.CreateForSharedStore(store), new AdvancingClock());
        var task = CreateTask();
        await service.AddAsync(task, TestContext.Current.CancellationToken);
        await service.StartAsync(task.Id, TestContext.Current.CancellationToken);
        await service.RecordTransferFileAsync(
            task.Id,
            "video-1",
            "segment.m4s",
            TestContext.Current.CancellationToken);
        await service.SetBackendIdentityAsync(
            task.Id,
            "aria-gid",
            TestContext.Current.CancellationToken);

        var result = await service.RecoverInterruptedAsync(
            task.Id,
            TestContext.Current.CancellationToken);

        var recovered = result.RequireValue();
        Assert.Equal(DownloadPhase.Queued, recovered.Phase);
        Assert.Equal("aria-gid", recovered.Transfer.BackendIdentity);
        Assert.Equal("segment.m4s", recovered.Plan.TransferFiles["video-1"]);
        Assert.Equal(4, recovered.Version);
        Assert.Equal([0L, 1L, 2L, 3L], store.ExpectedVersions);
    }

    [Fact]
    public async Task InterruptedReconciliationPreservesNewerPauseIntent()
    {
        var store = new RecordingStore();
        using var service = new DownloadTaskApplicationService(store, DownloadHistoryService.CreateForSharedStore(store), new AdvancingClock());
        var task = CreateTask();
        await service.AddAsync(task, TestContext.Current.CancellationToken);
        var snapshot = (await service.StartAsync(
            task.Id,
            TestContext.Current.CancellationToken)).RequireValue();
        await service.PauseAsync(task.Id, TestContext.Current.CancellationToken);

        var result = await service.ReconcileInterruptedAsync(
            task.Id,
            snapshot.Version,
            TestContext.Current.CancellationToken);

        var reconciled = result.RequireValue();
        Assert.Equal(DownloadPhase.Paused, reconciled.Phase);
        Assert.Equal(3, reconciled.Version);
        Assert.Equal([0L, 1L, 2L], store.ExpectedVersions);
    }

    [Fact]
    public async Task ArtifactClaimsPreservePriorPathsAndBackendIdentity()
    {
        var store = new RecordingStore();
        using var service = new DownloadTaskApplicationService(store, DownloadHistoryService.CreateForSharedStore(store), new AdvancingClock());
        var task = CreateTask();
        await service.AddAsync(task, TestContext.Current.CancellationToken);
        await service.StartAsync(task.Id, TestContext.Current.CancellationToken);
        await service.SetBackendIdentityAsync(
            task.Id,
            "aria-gid",
            TestContext.Current.CancellationToken);

        await service.ClaimTransferFileAsync(
            task.Id,
            "subtitle-0001",
            "episode_Chinese.srt",
            TestContext.Current.CancellationToken);
        await service.ClaimTransferFileAsync(
            task.Id,
            "subtitle-0001",
            "episode_Traditional-Chinese.srt",
            TestContext.Current.CancellationToken);
        await service.ClaimTransferFileAsync(
            task.Id,
            "subtitle-0001",
            "episode_Traditional-Chinese.srt",
            TestContext.Current.CancellationToken);

        var stored = Assert.IsType<DownloadTask>(store.Current);
        Assert.Equal("aria-gid", stored.Transfer.BackendIdentity);
        Assert.Equal(2, stored.Plan.TransferFiles.Count);
        Assert.Equal("episode_Chinese.srt", stored.Plan.TransferFiles["subtitle-0001"]);
        Assert.Contains("episode_Traditional-Chinese.srt", stored.Plan.TransferFiles.Values);
        Assert.Equal(task.Plan.NfoRequest, stored.Plan.NfoRequest);
    }

    [Fact]
    public async Task InvalidatingCompletedFileAlsoClearsBackendIdentity()
    {
        var store = new RecordingStore();
        using var service = new DownloadTaskApplicationService(store, DownloadHistoryService.CreateForSharedStore(store), new AdvancingClock());
        var task = CreateTask();
        await service.AddAsync(task, TestContext.Current.CancellationToken);
        await service.StartAsync(task.Id, TestContext.Current.CancellationToken);
        await service.RecordTransferFileAsync(
            task.Id,
            "video-1",
            "segment.m4s",
            TestContext.Current.CancellationToken);
        await service.CompleteTransferFileAsync(
            task.Id,
            "video-1",
            TestContext.Current.CancellationToken);
        await service.SetBackendIdentityAsync(
            task.Id,
            "stale-aria-gid",
            TestContext.Current.CancellationToken);

        var result = await service.InvalidateCompletedFileAsync(
            task.Id,
            "video-1",
            TestContext.Current.CancellationToken);

        var updated = result.RequireValue();
        Assert.Empty(updated.Transfer.CompletedFileKeys);
        Assert.Null(updated.Transfer.BackendIdentity);
        Assert.Equal("segment.m4s", updated.Plan.TransferFiles["video-1"]);
    }

    [Fact]
    public async Task InvalidatingCompletedFilesUsesOneDurableMutation()
    {
        var store = new RecordingStore();
        using var service = new DownloadTaskApplicationService(store, DownloadHistoryService.CreateForSharedStore(store), new AdvancingClock());
        var task = CreateTask();
        await service.AddAsync(task, TestContext.Current.CancellationToken);
        await service.StartAsync(task.Id, TestContext.Current.CancellationToken);
        foreach (var (key, fileName) in new[]
                 {
                     ("audio-1", "audio.m4s"),
                     ("video-1", "video.m4s"),
                     ("keep-1", "keep.m4s")
                 })
        {
            await service.RecordTransferFileAsync(
                task.Id,
                key,
                fileName,
                TestContext.Current.CancellationToken);
            await service.CompleteTransferFileAsync(
                task.Id,
                key,
                TestContext.Current.CancellationToken);
        }

        await service.SetBackendIdentityAsync(
            task.Id,
            "stale-aria-gid",
            TestContext.Current.CancellationToken);
        var updatesBeforeInvalidation = store.ExpectedVersions.Count;

        var result = await service.InvalidateCompletedFilesAsync(
            task.Id,
            ["audio-1", "video-1"],
            TestContext.Current.CancellationToken);

        var updated = result.RequireValue();
        Assert.Equal(updatesBeforeInvalidation + 1, store.ExpectedVersions.Count);
        Assert.Equal("keep-1", Assert.Single(updated.Transfer.CompletedFileKeys));
        Assert.Null(updated.Transfer.BackendIdentity);
        Assert.Equal("audio.m4s", updated.Plan.TransferFiles["audio-1"]);
        Assert.Equal("video.m4s", updated.Plan.TransferFiles["video-1"]);
    }

    [Fact]
    public async Task DiscardingUnpublishedArtifactsAndFailingUsesOneDurableMutation()
    {
        var store = new RecordingStore();
        using var service = new DownloadTaskApplicationService(
            store,
            DownloadHistoryService.CreateForSharedStore(store),
            new AdvancingClock());
        var task = CreateTask();
        await service.AddAsync(task, TestContext.Current.CancellationToken);
        await service.StartAsync(task.Id, TestContext.Current.CancellationToken);
        await service.SetBackendIdentityAsync(
            task.Id,
            "legacy-id",
            TestContext.Current.CancellationToken);
        var updatesBeforeFailure = store.ExpectedVersions.Count;
        var failure = new DownloadFailure("legacy.removed", "Legacy transfer removed.", true);

        var result = await service.DiscardUnpublishedArtifactsAndFailAsync(
            task.Id,
            failure,
            TestContext.Current.CancellationToken);

        var failed = result.RequireValue();
        Assert.Equal(updatesBeforeFailure + 1, store.ExpectedVersions.Count);
        Assert.Equal(DownloadPhase.Failed, failed.Phase);
        Assert.Same(failure, failed.Failure);
        Assert.Empty(failed.Plan.TransferFiles);
        Assert.Same(DownloadTransferState.LegacyEmpty, failed.Transfer);
        Assert.True(failed.Transfer.HasLegacyProvenance);
    }

    [Fact]
    public async Task InvalidCommandDoesNotPersistOrPublishAReplacementSnapshot()
    {
        var store = new RecordingStore();
        using var service = new DownloadTaskApplicationService(store, DownloadHistoryService.CreateForSharedStore(store), new AdvancingClock());
        var task = CreateTask();
        await service.AddAsync(task, TestContext.Current.CancellationToken);
        var eventCount = 0;
        service.TaskChanged += (_, _) => eventCount++;

        var result = await service.CompleteAsync(
            task.Id,
            new DownloadCompletion(1, "finished", null),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.transition.invalid", result.Error?.Code);
        Assert.Equal(0, eventCount);
        Assert.Same(task, store.Current);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConditionalFailureMutatesQueuedAndDownloadingTasks(bool startBeforeFailure)
    {
        var store = new RecordingStore();
        using var service = new DownloadTaskApplicationService(
            store,
            DownloadHistoryService.CreateForSharedStore(store),
            new AdvancingClock());
        var task = CreateTask();
        await service.AddAsync(task, TestContext.Current.CancellationToken);
        if (startBeforeFailure)
        {
            await service.StartAsync(task.Id, TestContext.Current.CancellationToken);
        }

        var updatesBeforeFailure = store.ExpectedVersions.Count;
        var eventCount = 0;
        service.TaskChanged += (_, _) => eventCount++;
        var failure = new DownloadFailure("download.runtime.failed", "Runtime failed.", true);

        var result = await service.FailIfRunnableAsync(
            task.Id,
            failure,
            TestContext.Current.CancellationToken);

        var failed = result.RequireValue();
        Assert.Equal(DownloadPhase.Failed, failed.Phase);
        Assert.Same(failure, failed.Failure);
        Assert.Equal(updatesBeforeFailure + 1, store.ExpectedVersions.Count);
        Assert.Equal(1, eventCount);
    }

    [Fact]
    public async Task ConditionalPhaseCommandsDoNotOverwriteACompetingState()
    {
        var store = new RecordingStore();
        using var service = new DownloadTaskApplicationService(
            store,
            DownloadHistoryService.CreateForSharedStore(store),
            new AdvancingClock());
        var task = CreateTask();
        await service.AddAsync(task, TestContext.Current.CancellationToken);
        await service.StartAsync(task.Id, TestContext.Current.CancellationToken);
        var pausing = (await service.PauseAsync(
            task.Id,
            TestContext.Current.CancellationToken)).RequireValue();
        var updatesBeforeCommands = store.ExpectedVersions.Count;
        var eventCount = 0;
        service.TaskChanged += (_, _) => eventCount++;

        var failureResult = await service.FailIfRunnableAsync(
            task.Id,
            new DownloadFailure("download.runtime.failed", "Runtime failed.", true),
            TestContext.Current.CancellationToken);

        Assert.Same(pausing, failureResult.RequireValue());
        Assert.Equal(updatesBeforeCommands, store.ExpectedVersions.Count);
        Assert.Equal(0, eventCount);

        var confirmed = (await service.ConfirmPausedIfPausingAsync(
            task.Id,
            TestContext.Current.CancellationToken)).RequireValue();

        Assert.Equal(DownloadPhase.Paused, confirmed.Phase);
        Assert.Equal(pausing.Version + 1, confirmed.Version);
        Assert.Equal(updatesBeforeCommands + 1, store.ExpectedVersions.Count);
        Assert.Equal(1, eventCount);

        var repeated = await service.ConfirmPausedIfPausingAsync(
            task.Id,
            TestContext.Current.CancellationToken);

        Assert.Same(confirmed, repeated.RequireValue());
        Assert.Equal(updatesBeforeCommands + 1, store.ExpectedVersions.Count);
        Assert.Equal(1, eventCount);
    }

    [Fact]
    public async Task CompletingTaskAtomicallyReplacesRecoverableStateWithLossyHistoryRecord()
    {
        var store = new RecordingStore();
        using var service = new DownloadTaskApplicationService(store, DownloadHistoryService.CreateForSharedStore(store), new AdvancingClock());
        var task = CreateTask();
        await service.AddAsync(task, TestContext.Current.CancellationToken);
        var started = (await service.StartAsync(
            task.Id,
            TestContext.Current.CancellationToken)).RequireValue();

        var completed = (await service.CompleteAsync(
            task.Id,
            new DownloadCompletion(123, "finished", "24 Mbps"),
            TestContext.Current.CancellationToken)).RequireValue();

        Assert.Null(store.Current);
        Assert.Equal(started.Version, store.CompletionExpectedVersion);
        Assert.NotNull(store.CompletedHistory);
        Assert.Equivalent(
            DownloadHistoryRecord.FromCompletedTask(completed),
            store.CompletedHistory,
            strict: true);
    }

    [Fact]
    public async Task CancellationWhileAnotherMutationIsLoadingDoesNotStartASecondCommand()
    {
        var store = new RecordingStore();
        using var service = new DownloadTaskApplicationService(
            store, DownloadHistoryService.CreateForSharedStore(store), new AdvancingClock());
        var task = CreateTask();
        Assert.True((await service.AddAsync(task, TestContext.Current.CancellationToken)).IsSuccess);
        var pendingFind = new TaskCompletionSource<DownloadTask?>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.NextFind = pendingFind;
        var starting = service.StartAsync(task.Id, TestContext.Current.CancellationToken);
        Assert.False(starting.IsCompleted);
        using var cancellation = new CancellationTokenSource();
        try
        {
            var waiting = service.PauseAsync(new DownloadTaskId("other-task"), cancellation.Token);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }
        finally
        {
            pendingFind.SetResult(task);
            Assert.True((await starting.ConfigureAwait(true)).IsSuccess);
        }

        Assert.Equal(DownloadPhase.Downloading, store.Current?.Phase);
        Assert.True((await service.PauseAsync(task.Id, TestContext.Current.CancellationToken)).IsSuccess);
        Assert.Equal(DownloadPhase.Pausing, store.Current?.Phase);
    }

    [Fact]
    public async Task CommittedSnapshotsAreVisibleBeforeEventsAndReleasedOnCompletion()
    {
        var store = new RecordingStore();
        using var service = new DownloadTaskApplicationService(store,
            DownloadHistoryService.CreateForSharedStore(store), new AdvancingClock());
        var events = new List<DownloadTaskChangedEventArgs>();
        service.TaskChanged += (sender, args) =>
        {
            if (args.Snapshot?.Phase == DownloadPhase.Completed)
                Assert.False(service.TryGetSnapshot(args.TaskId, out _));
            else
                Assert.Same(args.Snapshot, service.GetRequiredSnapshot(args.TaskId));
            events.Add(args);
        };
        var queued = CreateTask();
        (await service.AddAsync(queued, TestContext.Current.CancellationToken)).RequireValue();
        var started = (await service.StartAsync(queued.Id, TestContext.Current.CancellationToken)).RequireValue();

        Assert.Null(events[0].PreviousSnapshot);
        Assert.Same(queued, events[1].PreviousSnapshot);
        Assert.Same(started, service.GetRequiredSnapshot(queued.Id));
        (await service.CompleteAsync(queued.Id, new DownloadCompletion(1, "finished", null),
            TestContext.Current.CancellationToken)).RequireValue();

        Assert.Same(started, events[2].PreviousSnapshot);
        Assert.False(service.TryGetSnapshot(queued.Id, out _));
        Assert.NotNull(store.CompletedHistory);
    }

    [Fact]
    public async Task RejectedPersistenceKeepsTheLastCommittedSnapshot()
    {
        var store = new RecordingStore();
        using var service = new DownloadTaskApplicationService(store,
            DownloadHistoryService.CreateForSharedStore(store), new AdvancingClock());
        var queued = CreateTask();
        (await service.AddAsync(queued, TestContext.Current.CancellationToken)).RequireValue();
        store.UpdateError = new OperationError("test.write-failed", "Synthetic write failure.");
        var events = 0;
        service.TaskChanged += (_, _) => events++;

        var result = await service.StartAsync(queued.Id, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("test.write-failed", result.Error?.Code);
        Assert.Same(queued, service.GetRequiredSnapshot(queued.Id));
        Assert.Same(queued, store.Current);
        Assert.Equal(0, events);
    }

    [Fact]
    public async Task LoadingUnfinishedTasksRestoresSnapshotsWithoutPresentation()
    {
        var store = new RecordingStore();
        var paused = CreateTask().Pause(Epoch.AddSeconds(1)).RequireValue();
        Assert.True((await store.AddAsync(paused, TestContext.Current.CancellationToken)).IsSuccess);
        using var service = new DownloadTaskApplicationService(store,
            DownloadHistoryService.CreateForSharedStore(store), new AdvancingClock());
        Assert.False(service.TryGetSnapshot(paused.Id, out _));

        var restored = await service.GetUnfinishedAsync(TestContext.Current.CancellationToken);

        Assert.Same(paused, Assert.Single(restored));
        Assert.Same(paused, service.GetRequiredSnapshot(paused.Id));
        (await service.CancelAsync(paused.Id, TestContext.Current.CancellationToken)).RequireValue();
        (await service.DeleteAsync(paused.Id, TestContext.Current.CancellationToken)).RequireValue();
        Assert.False(service.TryGetSnapshot(paused.Id, out _));
    }

    [Fact]
    public async Task AnOlderUnfinishedReadCannotReintroduceACompletedSnapshot()
    {
        var store = new RecordingStore();
        using var service = new DownloadTaskApplicationService(store,
            DownloadHistoryService.CreateForSharedStore(store), new AdvancingClock());
        var queued = CreateTask();
        (await service.AddAsync(queued, TestContext.Current.CancellationToken)).RequireValue();
        var started = (await service.StartAsync(queued.Id, TestContext.Current.CancellationToken)).RequireValue();
        var pendingRead = new TaskCompletionSource<IReadOnlyList<DownloadTask>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        store.NextUnfinished = pendingRead;

        var loading = service.GetUnfinishedAsync(TestContext.Current.CancellationToken);
        var completing = service.CompleteAsync(queued.Id, new DownloadCompletion(1, "finished", null),
            TestContext.Current.CancellationToken);
        var completedBeforeRead = completing.IsCompleted;
        pendingRead.SetResult([started]);
        await Task.WhenAll(loading, completing);

        Assert.False(completedBeforeRead);
        Assert.True((await completing.ConfigureAwait(true)).IsSuccess);
        Assert.False(service.TryGetSnapshot(queued.Id, out _));
    }

    private static DownloadTask CreateTask()
    {
        return DownloadTask.Create(
            new DownloadTaskId("task-application-01"),
            new DownloadTaskMetadata(
                new DownloadMediaIdentity("BV1", 1, 2, 0, 1, 1),
                "Main",
                "Episode",
                "00:10",
                "AVC",
                new DownloadQuality(80, "1080P"),
                new DownloadQuality(30280, "AAC"),
                string.Empty,
                string.Empty,
                1),
            new DownloadPlan(
                new DownloadContentSelection(false, true, false, false, false),
                [],
                1,
                new DownloadNfoRequest(
                    "Title",
                    "Plot",
                    "2026",
                    ["Genre"],
                    ["Tag"],
                    [new DownloadNfoActor("Actor", "Role")],
                    new DownloadNfoUniqueId("bilibili", "BV1"),
                    "2026-09-09",
                    [new DownloadNfoRating("bilibili", 9, 10, true)])),
            new DownloadOutput("episode", null),
            Epoch);
    }

    private sealed class AdvancingClock : IClock
    {
        private long _ticks;

        public DateTimeOffset UtcNow => Epoch.AddSeconds(Interlocked.Increment(ref _ticks));

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            return Task.Delay(delay, cancellationToken);
        }
    }

    private sealed class RecordingStore :
        IDownloadTaskStore,
        IDownloadHistoryStore,
        IDownloadCompletionStore
    {
        private readonly Lock _sync = new();

        public DownloadTask? Current { get; private set; }

        public DownloadTask? ConcurrentUpdate { get; set; }

        public OperationError? UpdateError { get; set; }

        public TaskCompletionSource<DownloadTask?>? NextFind { get; set; }

        public TaskCompletionSource<IReadOnlyList<DownloadTask>>? NextUnfinished { get; set; }

        public List<long> ExpectedVersions { get; } = [];

        public int AddCount { get; private set; }

        public DownloadHistoryRecord? AddedHistory { get; private set; }

        public DownloadHistoryRecord? CompletedHistory { get; private set; }

        public long? CompletionExpectedVersion { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<OperationResult> AddAsync(DownloadTask task, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                AddCount++;
                if (Current != null)
                {
                    return Task.FromResult(OperationResult.Failure(new OperationError(
                        "download.store.conflict",
                        "Task already exists.",
                        OperationErrorKind.Conflict)));
                }

                Current = task;
                return Task.FromResult(OperationResult.Success());
            }
        }

        public Task<OperationResult> AddHistoryAsync(
            DownloadHistoryRecord history,
            CancellationToken cancellationToken)
        {
            AddedHistory = history;
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> UpdateAsync(
            DownloadTask task,
            long expectedVersion,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                ExpectedVersions.Add(expectedVersion);
                if (UpdateError is { } error)
                {
                    return Task.FromResult(OperationResult.Failure(error));
                }
                if (ConcurrentUpdate is { } concurrent)
                {
                    Current = concurrent;
                    ConcurrentUpdate = null;
                }

                if (Current?.Version != expectedVersion)
                {
                    return Task.FromResult(OperationResult.Failure(new OperationError(
                        "download.store.conflict",
                        "Version changed.",
                        OperationErrorKind.Conflict)));
                }

                Current = task.Phase == DownloadPhase.Deleted ? null : task;
                return Task.FromResult(OperationResult.Success());
            }
        }

        public Task<OperationResult> CompleteAsync(
            DownloadTask task,
            DownloadHistoryRecord history,
            long expectedVersion,
            CancellationToken cancellationToken)
        {
            ExpectedVersions.Add(expectedVersion);
            CompletedHistory = history;
            CompletionExpectedVersion = expectedVersion;
            Current = null;
            return Task.FromResult(OperationResult.Success());
        }

        public Task<DownloadTask?> FindAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (NextFind is { } pending)
                {
                    NextFind = null;
                    return pending.Task.WaitAsync(cancellationToken);
                }

                return Task.FromResult(Current?.Id == taskId ? Current : null);
            }
        }

        public Task<IReadOnlyList<DownloadTask>> GetUnfinishedAsync(CancellationToken cancellationToken)
        {
            if (NextUnfinished is { } pending)
            {
                NextUnfinished = null;
                return pending.Task.WaitAsync(cancellationToken);
            }
            return Task.FromResult<IReadOnlyList<DownloadTask>>(Current == null ? [] : [Current]);
        }

        public Task<bool> IsOutputPathReservedAsync(
            string basePath,
            bool ignoreCase,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<IReadOnlyList<string>> GetActiveOutputReservationKeysAsync(
            bool ignoreCase,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(Current == null ? [] : [DownloadOutputPathKey.Create(Current.Output.BasePath, ignoreCase)]);

        public Task<DownloadHistoryPage> GetHistoryPageAsync(
            DownloadHistoryCursor? cursor,
            int pageSize,
            CancellationToken cancellationToken,
            DownloadFinishedSort sort = DownloadFinishedSort.DownloadDesc) =>
            Task.FromResult(new DownloadHistoryPage([], null));

        public Task<OperationResult> DeleteAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken)
        {
            Current = null;
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> DeleteHistoryAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Success());

        public Task<OperationResult> ClearHistoryAsync(CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Success());

        public Task<IReadOnlyList<QuarantinedDownloadRecord>> GetQuarantinedRecordsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<QuarantinedDownloadRecord>>([]);
    }
}
