using DownKyi.Application.Downloads;
using DownKyi.Infrastructure.Downloads;
using DownKyi.Infrastructure.Time;
using DownKyi.Models;
using DownKyi.Services.Download;
using DownKyi.ViewModels.DownloadManager;

namespace DownKyi.Tests;

public sealed class DownloadBatchAdmissionTests
{
    [Fact]
    public async Task SameBaseNameBatchGetsDistinctReservationsAndQueuesBoth()
    {
        var cancellationToken =
            TestContext.Current.CancellationToken;

        var root =
            Path.Combine(
                Path.GetTempPath(),
                "downkyi-batch-admission-tests",
                Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(root);

        try
        {
            var database =
                Path.Combine(
                    root,
                    "downloads.db");

            using var store =
                new SqliteDownloadTaskStore(
                    new SqliteDownloadTaskStoreOptions(
                        database),
                    new SystemClock());

            using var tasks =
                new DownloadTaskApplicationService(
                    store,
                    new SystemClock());

            using var projections =
                new DownloadTaskProjectionStore(
                    tasks,
                    new SystemClock());

            var lists =
                new DownloadListState();

            var queue =
                new RecordingDownloadTaskQueue();

            using var admission =
                new DownloadTaskAdmissionService(
                    lists,
                    tasks,
                    projections,
                    queue);

            var basePath =
                Path.Combine(
                    root,
                    "same-output");

            var first =
                CreateItem(
                    "batch-1",
                    basePath,
                    1);

            var second =
                CreateItem(
                    "batch-2",
                    basePath,
                    2);

            await admission
                .AdmitManyAsync(
                    new[] { first, second },
                    autoAddNumberSuffix: true,
                    cancellationToken)
                .ConfigureAwait(true);

            Assert.Equal(
                basePath,
                first.DownloadBase.FilePath);

            Assert.Equal(
                basePath + "(1)",
                second.DownloadBase.FilePath);

            var unfinished =
                await tasks
                    .GetUnfinishedAsync(
                        cancellationToken)
                    .ConfigureAwait(true);

            Assert.Equal(
                2,
                unfinished.Count);

            Assert.Equal(
                2,
                lists.Downloading.Count);

            Assert.Equal(
                2,
                queue.Enqueued.Count);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection
                .ClearAllPools();

            if (Directory.Exists(root))
            {
                Directory.Delete(
                    root,
                    recursive: true);
            }
        }
    }

    private static DownloadingItem CreateItem(
        string id,
        string filePath,
        long cid)
    {
        return new DownloadingItem
        {
            DownloadBase =
                new DownloadBase
                {
                    Id = id,
                    Avid = cid,
                    Bvid = $"BV-{id}",
                    Cid = cid,
                    FilePath = filePath,
                    Name = id,
                    Resolution =
                        new DownKyi.Core.BiliApi.BiliUtils.Quality
                        {
                            Id = 80,
                            Name = "1080P"
                        },
                    VideoCodecName = "AVC"
                },
            Downloading =
                new Downloading
                {
                    DownloadStatus =
                        DownloadStatus.NotStarted,
                    PlayStreamType =
                        DownKyi.Core.BiliApi.VideoStream
                            .PlayStreamType.Video
                },
            PlayUrl =
                new DownKyi.Core.BiliApi.VideoStream.Models
                    .PlayUrl()
        };
    }
}