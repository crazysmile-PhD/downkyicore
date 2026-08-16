using DownKyi.Application.Downloads;
using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
using DownKyi.Infrastructure.Downloads;
using DownKyi.Infrastructure.Time;
using DownKyi.Models;
using DownKyi.Services.Download;
using DownKyi.ViewModels.DownloadManager;
using Microsoft.Data.Sqlite;

namespace DownKyi.Tests;

public sealed class DownloadTaskAdmissionCrossInstanceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "downkyi-cross-instance-admission",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AutoSuffixOnRetriesAfterIndependentDurableReservationConflict()
    {
        using var session = await AdmissionSession.CreateAsync(_directory).ConfigureAwait(true);
        var requestedPath = Path.Combine(_directory, "same-output");

        var first = CreateItem("first", requestedPath);
        var second = CreateItem("second", requestedPath);

        await Task.WhenAll(
            session.First.AdmitAsync(first, autoAddNumberSuffix: true, TestContext.Current.CancellationToken),
            session.Second.AdmitAsync(second, autoAddNumberSuffix: true, TestContext.Current.CancellationToken))
            .ConfigureAwait(true);

        Assert.Equal(
            [Path.GetFullPath(requestedPath), Path.GetFullPath(requestedPath) + "(1)"],
            new[] { first.DownloadBase.FilePath, second.DownloadBase.FilePath }
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray());
    }

    [Fact]
    public async Task AutoSuffixOffAllowsExactlyOneIndependentDurableReservationOwner()
    {
        using var session = await AdmissionSession.CreateAsync(_directory).ConfigureAwait(true);
        var requestedPath = Path.Combine(_directory, "same-output");

        var first = CreateItem("first", requestedPath);
        var second = CreateItem("second", requestedPath);

        var results = await Task.WhenAll(
            CaptureIOExceptionAsync(() => session.First.AdmitAsync(first, autoAddNumberSuffix: false, TestContext.Current.CancellationToken)),
            CaptureIOExceptionAsync(() => session.Second.AdmitAsync(second, autoAddNumberSuffix: false, TestContext.Current.CancellationToken)))
            .ConfigureAwait(true);

        Assert.Single(results, result => result == null);
        Assert.Single(results, result => result is IOException);
        var unfinished = await session.Tasks.GetUnfinishedAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Single(unfinished);
        Assert.Equal(Path.GetFullPath(requestedPath), unfinished[0].Output.BasePath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private static async Task<IOException?> CaptureIOExceptionAsync(Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(true);
            return null;
        }
        catch (IOException exception)
        {
            return exception;
        }
    }

    private static DownloadingItem CreateItem(string id, string basePath)
    {
        return new DownloadingItem
        {
            DownloadBase = new DownloadBase
            {
                Id = id,
                Bvid = $"BV-{id}",
                MainTitle = id,
                Name = id,
                FilePath = basePath
            },
            Downloading = new Downloading
            {
                Id = id,
                DownloadStatus = DownloadStatus.WaitForDownload
            },
            PlayUrl = new PlayUrl()
        };
    }

    private sealed class AdmissionSession : IDisposable
    {
        private readonly SqliteDownloadTaskStore _firstStore;
        private readonly SqliteDownloadTaskStore _secondStore;
        private readonly DownloadTaskProjectionStore _firstProjections;
        private readonly DownloadTaskProjectionStore _secondProjections;
        private readonly DownloadTaskAdmissionService _firstAdmission;
        private readonly DownloadTaskAdmissionService _secondAdmission;

        private AdmissionSession(
            SqliteDownloadTaskStore firstStore,
            SqliteDownloadTaskStore secondStore,
            DownloadTaskApplicationService firstTasks,
            DownloadTaskApplicationService secondTasks,
            DownloadTaskProjectionStore firstProjections,
            DownloadTaskProjectionStore secondProjections,
            DownloadTaskAdmissionService firstAdmission,
            DownloadTaskAdmissionService secondAdmission)
        {
            _firstStore = firstStore;
            _secondStore = secondStore;
            First = firstAdmission;
            Second = secondAdmission;
            Tasks = firstTasks;
            _firstProjections = firstProjections;
            _secondProjections = secondProjections;
            _firstAdmission = firstAdmission;
            _secondAdmission = secondAdmission;
            FirstTasks = firstTasks;
            SecondTasks = secondTasks;
        }

        public DownloadTaskAdmissionService First { get; }

        public DownloadTaskAdmissionService Second { get; }

        public DownloadTaskApplicationService Tasks { get; }

        private DownloadTaskApplicationService FirstTasks { get; }

        private DownloadTaskApplicationService SecondTasks { get; }

        public static async Task<AdmissionSession> CreateAsync(string directory)
        {
            Directory.CreateDirectory(directory);
            var databasePath = Path.Combine(directory, "download.db");
            var firstStore = new SqliteDownloadTaskStore(
                new SqliteDownloadTaskStoreOptions(databasePath), new SystemClock());
            var secondStore = new SqliteDownloadTaskStore(
                new SqliteDownloadTaskStoreOptions(databasePath), new SystemClock());
            var addGate = new ConcurrentAddGate();
            var firstTasks = new DownloadTaskApplicationService(
                new SynchronizedAddStore(firstStore, addGate), new SystemClock());
            var secondTasks = new DownloadTaskApplicationService(
                new SynchronizedAddStore(secondStore, addGate), new SystemClock());
            var firstProjections = new DownloadTaskProjectionStore(firstTasks, new SystemClock());
            var secondProjections = new DownloadTaskProjectionStore(secondTasks, new SystemClock());
            var firstAdmission = new DownloadTaskAdmissionService(
                new DownloadListState(), firstTasks, firstProjections, new RecordingDownloadTaskQueue());
            var secondAdmission = new DownloadTaskAdmissionService(
                new DownloadListState(), secondTasks, secondProjections, new RecordingDownloadTaskQueue());
            await firstStore.InitializeAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            await secondStore.InitializeAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            return new AdmissionSession(
                firstStore, secondStore, firstTasks, secondTasks, firstProjections, secondProjections,
                firstAdmission, secondAdmission);
        }

        public void Dispose()
        {
            _firstAdmission.Dispose();
            _secondAdmission.Dispose();
            _firstProjections.Dispose();
            _secondProjections.Dispose();
            FirstTasks.Dispose();
            SecondTasks.Dispose();
            _firstStore.Dispose();
            _secondStore.Dispose();
        }
    }

    private sealed class ConcurrentAddGate
    {
        private readonly TaskCompletionSource _bothAddsReached = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        public Task WaitForFirstPairAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _arrivals) == 2)
            {
                _bothAddsReached.TrySetResult();
            }

            return _bothAddsReached.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class SynchronizedAddStore(IDownloadTaskStore inner, ConcurrentAddGate addGate)
        : IDownloadTaskStore
    {
        private int _addCalls;

        public Task InitializeAsync(CancellationToken cancellationToken) =>
            inner.InitializeAsync(cancellationToken);

        public async Task<OperationResult> AddAsync(
            DownloadTask task,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _addCalls) <= 1)
            {
                await addGate.WaitForFirstPairAsync(cancellationToken).ConfigureAwait(false);
            }

            return await inner.AddAsync(task, cancellationToken).ConfigureAwait(false);
        }

        public Task<OperationResult> UpdateAsync(
            DownloadTask task,
            long expectedVersion,
            CancellationToken cancellationToken) =>
            inner.UpdateAsync(task, expectedVersion, cancellationToken);

        public Task<OperationResult> UpdateProgressAsync(
            DownloadProgressWrite progressWrite,
            CancellationToken cancellationToken) =>
            inner.UpdateProgressAsync(progressWrite, cancellationToken);

        public Task<DownloadTask?> FindAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken) =>
            inner.FindAsync(taskId, cancellationToken);

        public Task<IReadOnlyList<DownloadTask>> GetUnfinishedAsync(
            CancellationToken cancellationToken) =>
            inner.GetUnfinishedAsync(cancellationToken);

        public Task<IReadOnlyList<string>> GetActiveOutputPathsAsync(
            CancellationToken cancellationToken) =>
            inner.GetActiveOutputPathsAsync(cancellationToken);

        public Task<bool> IsOutputPathReservedAsync(
            string basePath,
            bool ignoreCase,
            CancellationToken cancellationToken) =>
            inner.IsOutputPathReservedAsync(basePath, ignoreCase, cancellationToken);

        public Task<DownloadHistoryPage> GetHistoryPageAsync(
            DownloadHistoryCursor? cursor,
            int pageSize,
            CancellationToken cancellationToken) =>
            inner.GetHistoryPageAsync(cursor, pageSize, cancellationToken);

        public Task<OperationResult> DeleteAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken) =>
            inner.DeleteAsync(taskId, cancellationToken);

        public Task<OperationResult> ClearHistoryAsync(CancellationToken cancellationToken) =>
            inner.ClearHistoryAsync(cancellationToken);

        public Task<IReadOnlyList<QuarantinedDownloadRecord>> GetQuarantinedRecordsAsync(
            CancellationToken cancellationToken) =>
            inner.GetQuarantinedRecordsAsync(cancellationToken);
    }
}
