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
                1),
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

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<OperationResult> AddAsync(DownloadTask task, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
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
