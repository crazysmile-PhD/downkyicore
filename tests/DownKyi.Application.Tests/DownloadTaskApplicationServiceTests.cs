using DownKyi.Application.Downloads;
using DownKyi.Application.Time;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;

namespace DownKyi.Application.Tests;

public sealed class DownloadTaskApplicationServiceTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 7, 22, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CommandsPersistAggregateBeforePublishingProjectionEvent()
    {
        var store = new RecordingStore();
        using var service = new DownloadTaskApplicationService(store, new AdvancingClock());
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
        using var service = new DownloadTaskApplicationService(store, new AdvancingClock());
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
    public async Task ArtifactClaimsPreservePriorPathsAndBackendIdentity()
    {
        var store = new RecordingStore();
        using var service = new DownloadTaskApplicationService(store, new AdvancingClock());
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
        using var service = new DownloadTaskApplicationService(store, new AdvancingClock());
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
        using var service = new DownloadTaskApplicationService(store, new AdvancingClock());
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
    public async Task InvalidCommandDoesNotPersistOrPublishAReplacementSnapshot()
    {
        var store = new RecordingStore();
        using var service = new DownloadTaskApplicationService(store, new AdvancingClock());
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

    [Fact]
    public async Task LegacyUpgradeAdmissionStateAndConfirmationDelegateToStore()
    {
        var store = new RecordingStore { LegacyUpgradeAdmissionBlocked = true };
        using var service = new DownloadTaskApplicationService(store, new AdvancingClock());

        Assert.True(await service.IsLegacyUpgradeAdmissionBlockedAsync(
            TestContext.Current.CancellationToken));
        Assert.True((await service.ConfirmLegacyRemoteTasksStoppedAsync(
            TestContext.Current.CancellationToken)).IsSuccess);

        Assert.False(await service.IsLegacyUpgradeAdmissionBlockedAsync(
            TestContext.Current.CancellationToken));
        Assert.Equal(1, store.LegacyRemoteStopConfirmationCount);
    }

    [Fact]
    public async Task BlockedLegacyUpgradeRejectsQueuedTaskBeforeStoreAddAndAllowsSameSessionAfterConfirmation()
    {
        var store = new RecordingStore { LegacyUpgradeAdmissionBlocked = true };
        using var service = new DownloadTaskApplicationService(store, new AdvancingClock());

        var admission = await service.CheckNewDownloadAdmissionAsync(
            TestContext.Current.CancellationToken);
        var blocked = await service.AddAsync(
            CreateTask(),
            TestContext.Current.CancellationToken);

        Assert.False(admission.IsSuccess);
        Assert.True(DownloadAdmissionErrors.IsLegacyUpgradeBlocked(admission.Error));
        Assert.False(blocked.IsSuccess);
        Assert.True(DownloadAdmissionErrors.IsLegacyUpgradeBlocked(blocked.Error));
        Assert.Equal(0, store.AddCount);
        Assert.Null(store.Current);

        Assert.True((await service.ConfirmLegacyRemoteTasksStoppedAsync(
            TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await service.AddAsync(
            CreateTask(),
            TestContext.Current.CancellationToken)).IsSuccess);
        Assert.Equal(1, store.AddCount);
    }

    [Fact]
    public async Task BlockedLegacyUpgradeDoesNotRejectCompletedHistoryImport()
    {
        var store = new RecordingStore { LegacyUpgradeAdmissionBlocked = true };
        using var service = new DownloadTaskApplicationService(store, new AdvancingClock());
        var started = CreateTask().Start(Epoch.AddSeconds(1)).RequireValue();
        var completed = started.Complete(
            new DownloadCompletion(1, "finished", null),
            Epoch.AddSeconds(2)).RequireValue();

        var result = await service.AddAsync(completed, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, store.AddCount);
        Assert.Same(completed, store.Current);
        Assert.True(store.LegacyUpgradeAdmissionBlocked);
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
                new Dictionary<string, bool> { ["downloadVideo"] = true },
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

    private sealed class RecordingStore : IDownloadTaskStore
    {
        private readonly Lock _sync = new();

        public DownloadTask? Current { get; private set; }

        public List<long> ExpectedVersions { get; } = [];

        public bool LegacyUpgradeAdmissionBlocked { get; set; }

        public int LegacyRemoteStopConfirmationCount { get; private set; }

        public int AddCount { get; private set; }

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

        public Task<OperationResult> UpdateAsync(
            DownloadTask task,
            long expectedVersion,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                ExpectedVersions.Add(expectedVersion);
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

        public Task<OperationResult> UpdateProgressAsync(
            DownloadProgressWrite progressWrite,
            CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Success());

        public Task<DownloadTask?> FindAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                return Task.FromResult(Current?.Id == taskId ? Current : null);
            }
        }

        public Task<IReadOnlyList<DownloadTask>> GetUnfinishedAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DownloadTask>>(Current == null ? [] : [Current]);

        public Task<bool> IsOutputPathReservedAsync(
            string basePath,
            bool ignoreCase,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<bool> IsLegacyUpgradeAdmissionBlockedAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(LegacyUpgradeAdmissionBlocked);

        public Task<OperationResult> ConfirmLegacyRemoteTasksStoppedAsync(
            CancellationToken cancellationToken)
        {
            LegacyRemoteStopConfirmationCount++;
            LegacyUpgradeAdmissionBlocked = false;
            return Task.FromResult(OperationResult.Success());
        }

        public Task<DownloadHistoryPage> GetHistoryPageAsync(
            DownloadHistoryCursor? cursor,
            int pageSize,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DownloadHistoryPage([], null));

        public Task<OperationResult> DeleteAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken)
        {
            Current = null;
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> ClearHistoryAsync(CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Success());

        public Task<IReadOnlyList<QuarantinedDownloadRecord>> GetQuarantinedRecordsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<QuarantinedDownloadRecord>>([]);
    }
}
