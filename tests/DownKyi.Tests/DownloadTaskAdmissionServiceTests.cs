using DownKyi.Application.Downloads;
using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Domain.Downloads;
using DownKyi.Infrastructure.Downloads;
using DownKyi.Infrastructure.Time;
using DownKyi.Models;
using DownKyi.Services.Download;
using DownKyi.ViewModels.DownloadManager;

namespace DownKyi.Tests;

public sealed class DownloadTaskAdmissionServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "downkyi-admission-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ConcurrentAdmissionsPersistDistinctOutputPathsBeforeQueueing()
    {
        Directory.CreateDirectory(_directory);
        using var store = CreateStore();
        var clock = new SystemClock();
        using var tasks = new DownloadTaskApplicationService(store, clock);
        using var projections = new DownloadTaskProjectionStore(tasks, clock);
        var listState = new DownloadListState();
        var queue = new RecordingDownloadTaskQueue();
        using var admission = new DownloadTaskAdmissionService(listState, tasks, projections, queue);
        var basePath = Path.Combine(_directory, "same-output");
        var first = CreateItem("first", basePath);
        var second = CreateItem("second", basePath);

        await Task.WhenAll(
            admission.AdmitAsync(first, TestContext.Current.CancellationToken),
            admission.AdmitAsync(second, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        var persisted = await tasks
            .GetUnfinishedAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        var persistedPaths = persisted.Select(task => task.Output.BasePath).ToArray();
        Assert.Equal(2, persistedPaths.Distinct(DownloadOutputPathResolver.PlatformComparer).Count());
        Assert.Contains(basePath, persistedPaths);
        Assert.Contains($"{basePath}(1)", persistedPaths);
        Assert.Equal(2, listState.Downloading.Count);
        Assert.Equal(2, queue.Enqueued.Count);
    }

    [Fact]
    public async Task FailedRetryableTaskRetainsItsOutputReservation()
    {
        Directory.CreateDirectory(_directory);
        using var store = CreateStore();
        var clock = new SystemClock();
        using var tasks = new DownloadTaskApplicationService(store, clock);
        using var projections = new DownloadTaskProjectionStore(tasks, clock);
        using var admission = new DownloadTaskAdmissionService(
            new DownloadListState(),
            tasks,
            projections,
            new RecordingDownloadTaskQueue());
        var basePath = Path.Combine(_directory, "retryable-output");
        var first = CreateItem("failed", basePath);
        await admission.AdmitAsync(first, TestContext.Current.CancellationToken).ConfigureAwait(true);
        var taskId = new DownloadTaskId(first.DownloadBase.Id);
        Assert.True((await tasks
            .StartAsync(taskId, TestContext.Current.CancellationToken)
            .ConfigureAwait(true)).IsSuccess);
        Assert.True((await tasks
            .FailAsync(
                taskId,
                new DownloadFailure("download.failed", "Transfer failed.", true),
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true)).IsSuccess);

        var second = CreateItem("second", basePath);
        await admission.AdmitAsync(second, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal($"{basePath}(1)", second.DownloadBase.FilePath);
    }

    [Fact]
    public async Task CanceledTaskReleasesItsOutputReservation()
    {
        Directory.CreateDirectory(_directory);
        using var store = CreateStore();
        var clock = new SystemClock();
        using var tasks = new DownloadTaskApplicationService(store, clock);
        using var projections = new DownloadTaskProjectionStore(tasks, clock);
        using var admission = new DownloadTaskAdmissionService(
            new DownloadListState(),
            tasks,
            projections,
            new RecordingDownloadTaskQueue());
        var basePath = Path.Combine(_directory, "canceled-output");
        var first = CreateItem("canceled", basePath);
        await admission.AdmitAsync(first, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.True((await tasks
            .CancelAsync(new DownloadTaskId(first.DownloadBase.Id), TestContext.Current.CancellationToken)
            .ConfigureAwait(true)).IsSuccess);

        var second = CreateItem("replacement", basePath);
        await admission.AdmitAsync(second, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(basePath, second.DownloadBase.FilePath);
    }

    [Fact]
    public async Task AdmissionSkipsDiskCollisionCreatedAfterDraftConstruction()
    {
        Directory.CreateDirectory(_directory);
        using var store = CreateStore();
        var clock = new SystemClock();
        using var tasks = new DownloadTaskApplicationService(store, clock);
        using var projections = new DownloadTaskProjectionStore(tasks, clock);
        using var admission = new DownloadTaskAdmissionService(
            new DownloadListState(),
            tasks,
            projections,
            new RecordingDownloadTaskQueue());
        var basePath = Path.Combine(_directory, "late-disk-collision");
        var draft = CreateItem("draft", basePath);
        await File.WriteAllTextAsync(
            $"{basePath}.mp4",
            "foreign output",
            TestContext.Current.CancellationToken);

        await admission.AdmitAsync(draft, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal($"{basePath}(1)", draft.DownloadBase.FilePath);
    }

    [Fact]
    public void ActiveCollisionUsesCaseInsensitiveComparisonAndSkipsExistingSuffix()
    {
        Directory.CreateDirectory(_directory);
        var basePath = Path.Combine(_directory, "video");
        File.WriteAllText($"{basePath}(1).mp4", "occupied");

        var resolved = DownloadOutputPathResolver.ResolveActiveCollision(
            basePath,
            [Path.Combine(_directory, "VIDEO")],
            StringComparer.OrdinalIgnoreCase);

        Assert.Equal($"{basePath}(2)", resolved);
    }

    private SqliteDownloadTaskStore CreateStore()
    {
        return new SqliteDownloadTaskStore(
            new SqliteDownloadTaskStoreOptions(Path.Combine(_directory, "download.db")),
            new SystemClock());
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

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
