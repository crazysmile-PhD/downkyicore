using DownKyi.Application.Downloads;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Domain.Downloads;
using DownKyi.Infrastructure.Downloads;
using DownKyi.Infrastructure.Time;
using DownKyi.Models;
using DownKyi.Services.Download;
using DownKyi.ViewModels.DownloadManager;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class DurlDownloadIdentityTests
{
    private static readonly string[] BackupAddresses = { "https://backup.invalid/segment-7" };

    [Fact]
    public void DescriptorUsesDurlOrderAsStableDownloadKey()
    {
        var descriptor = DownloadMediaStage.CreateDurlDownloadDescriptor(new List<PlayUrlDurl>
        {
            new()
            {
                Order = 7,
                SourceAddress = "https://example.invalid/segment-7",
                BackupUrl = BackupAddresses,
                Size = 4096
            }
        });

        Assert.NotNull(descriptor);
        Assert.Equal(7, descriptor.Id);
        Assert.Equal("durl", descriptor.Codecs);
        Assert.Equal("7_durl", DownloadTransferKey.Create(descriptor.Id, descriptor.Codecs));
        Assert.Equal("https://example.invalid/segment-7", descriptor.BaseAddress);
        Assert.Equal(4096, descriptor.ExpectedSize);
    }

    [Fact]
    public void DescriptorSelectsLowestDurlOrder()
    {
        var descriptor = DownloadMediaStage.CreateDurlDownloadDescriptor(new List<PlayUrlDurl>
        {
            new() { Order = 9, SourceAddress = "https://example.invalid/segment-9" },
            new() { Order = 2, SourceAddress = "https://example.invalid/segment-2" },
            new() { Order = 5, SourceAddress = "https://example.invalid/segment-5" }
        });

        Assert.NotNull(descriptor);
        Assert.Equal(2, descriptor.Id);
        Assert.Equal("2_durl", DownloadTransferKey.Create(descriptor.Id, descriptor.Codecs));
        Assert.Equal("https://example.invalid/segment-2", descriptor.BaseAddress);
    }

    [Theory]
    [InlineData("https://i0.example.invalid/cover.jpg?token=redacted", "jpg")]
    [InlineData("//i0.example.invalid/cover.webp@672w_378h.webp?token=redacted", "webp")]
    [InlineData("images/cover.png#thumbnail", "png")]
    public void CoverExtensionIgnoresUriQueryAndFragment(string source, string expected)
    {
        Assert.Equal(expected, DownloadArtifactsStage.GetImageExtension(source));
    }

    [Fact]
    public void DownloadDirectoryUsesPathSemantics()
    {
        var filePath = Path.Combine("downloads", "nested", "video");

        Assert.Equal(
            Path.Combine("downloads", "nested"),
            ResolvePlaybackStage.GetDownloadDirectoryPath(filePath));
    }

    [Fact]
    public async Task BangumiPlaybackStageRequestsFinalizedQualityWithoutInitialPlayback()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "downkyi-bangumi-playback-refresh-tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "download.db");
        Directory.CreateDirectory(directory);
        try
        {
            var downloadBase = new DownloadBase
            {
                Id = "bangumi-final-quality",
                Avid = 1,
                Bvid = "BV1fixture",
                Cid = 2,
                EpisodeId = 3489,
                FilePath = Path.Combine(directory, "video"),
                Resolution = new DownKyi.Core.BiliApi.BiliUtils.Quality
                {
                    Id = 112,
                    Name = "1080P+"
                },
                VideoCodecName = "H.265/HEVC",
                NeedDownloadContent = DownloadContentSelection.None with
                {
                    Video = true,
                    MediaKind = DownloadMediaKind.Dash
                }
            };
            var downloading = new DownloadingItem
            {
                DownloadBase = downloadBase,
                Downloading = new Downloading
                {
                    Id = downloadBase.Id,
                    DownloadBase = downloadBase,
                    PlayStreamType = PlayStreamType.Bangumi,
                    DownloadStatus = DownloadStatus.WaitForDownload
                }
            };
            string? requestedAddress = null;
            var client = new TestBilibiliApiClient
            {
                GetStringAsyncHandler = (request, _) =>
                {
                    requestedAddress = request.RequestAddress;
                    return Task.FromResult(
                        """
                        {"code":0,"result":{"video_info":{"quality":112,"durl":[],"dash":{"video":[{"id":112,"codecid":12,"base_url":"https://media.invalid/video-112"}],"audio":[{"id":30280,"base_url":"https://media.invalid/audio"}]}}}}
                        """);
                }
            };
            using var store = new SqliteDownloadTaskStore(
                new SqliteDownloadTaskStoreOptions(databasePath),
                new SystemClock());
            var clock = new SystemClock();
            var historyService = DownloadHistoryService.CreateForSharedStore(store);
            using var tasks = new DownloadTaskApplicationService(store, historyService, clock);
            using var projectionStore = new DownloadTaskProjectionStore(
                tasks,
                historyService,
                clock);
            using var settings = new TestSettingsStore();
            var taskId = new DownloadTaskId(downloadBase.Id);
            var stateWriter = new DownloadTaskStateWriter(tasks);
            await projectionStore.AddDownloadingAsync(
                downloading,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            await stateWriter.StartAsync(taskId, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            var stage = new ResolvePlaybackStage(
                new TestDesktopInteractionContext().Notifications,
                new DownloadActivityPresenter(projectionStore, stateWriter),
                new DownloadPlaybackResolver(
                    new TestWbiKeyProvider(),
                    TimeProvider.System,
                    client),
                NullLogger<ResolvePlaybackStage>.Instance);
            var context = new DownloadExecutionContextFactory(
                projectionStore,
                settings.Store).Create(taskId);

            var result = await stage.ExecuteAsync(
                context,
                TestContext.Current.CancellationToken);

            Assert.True(result.IsSuccess);
            Assert.Contains("qn=112", requestedAddress, StringComparison.Ordinal);
            Assert.Equal(112, Assert.Single(context.PlayUrl!.Dash.Video).Id);
        }
        finally
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = true,
                DefaultTimeout = 5
            }.ToString());
            SqliteConnection.ClearPool(connection);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PlaybackStageUsesLegacySeparatorsWithoutRewritingFrozenBasePath()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "downkyi-playback-path-tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "download.db");
        Directory.CreateDirectory(directory);
        try
        {
            var frozenBasePath = Path.Combine(directory, "legacy\\nested\\video");
            var expectedDirectory = Path.Combine(directory, "legacy", "nested");
            var downloadBase = new DownloadBase
            {
                Id = "frozen-playback-path",
                FilePath = frozenBasePath,
                NeedDownloadContent = DownloadContentSelection.None with
                {
                    MediaKind = DownloadMediaKind.None
                }
            };
            var downloading = new DownloadingItem
            {
                DownloadBase = downloadBase,
                Downloading = new Downloading
                {
                    Id = downloadBase.Id,
                    DownloadBase = downloadBase,
                    DownloadStatus = DownloadStatus.WaitForDownload
                }
            };
            using var store = new SqliteDownloadTaskStore(
                new SqliteDownloadTaskStoreOptions(databasePath),
                new SystemClock());
            var clock = new SystemClock();
            var historyService = DownloadHistoryService.CreateForSharedStore(store);
            using var tasks = new DownloadTaskApplicationService(store, historyService, clock);
            using var projectionStore = new DownloadTaskProjectionStore(
                tasks,
                historyService,
                clock);
            using var settings = new TestSettingsStore();
            var taskId = new DownloadTaskId(downloadBase.Id);
            var stateWriter = new DownloadTaskStateWriter(tasks);
            await projectionStore.AddDownloadingAsync(
                downloading,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            await stateWriter.StartAsync(taskId, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            var stage = new ResolvePlaybackStage(
                new TestDesktopInteractionContext().Notifications,
                new DownloadActivityPresenter(projectionStore, stateWriter),
                new DownloadPlaybackResolver(
                    new TestWbiKeyProvider(),
                    TimeProvider.System,
                    new TestBilibiliApiClient()),
                NullLogger<ResolvePlaybackStage>.Instance);
            var context = new DownloadExecutionContextFactory(
                projectionStore,
                settings.Store).Create(taskId);

            var result = await stage.ExecuteAsync(
                context,
                TestContext.Current.CancellationToken);

            Assert.True(result.IsSuccess);
            Assert.Equal(expectedDirectory, context.DownloadDirectory);
            Assert.Equal(frozenBasePath, downloading.DownloadBase.FilePath, ignoreCase: false);
        }
        finally
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = true,
                DefaultTimeout = 5
            }.ToString());
            SqliteConnection.ClearPool(connection);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PlaybackStageRequiresLegacyUnfinishedTaskToBeRecreated()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "downkyi-legacy-contract-tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "download.db");
        Directory.CreateDirectory(directory);
        try
        {
            var downloadBase = new DownloadBase
            {
                Id = "legacy-without-media-kind",
                FilePath = Path.Combine(directory, "video")
            };
            var downloading = new DownloadingItem
            {
                DownloadBase = downloadBase,
                Downloading = new Downloading
                {
                    Id = downloadBase.Id,
                    DownloadBase = downloadBase,
                    DownloadStatus = DownloadStatus.WaitForDownload
                }
            };
            using var store = new SqliteDownloadTaskStore(
                new SqliteDownloadTaskStoreOptions(databasePath),
                new SystemClock());
            var clock = new SystemClock();
            var historyService = DownloadHistoryService.CreateForSharedStore(store);
            using var tasks = new DownloadTaskApplicationService(store, historyService, clock);
            using var projectionStore = new DownloadTaskProjectionStore(
                tasks,
                historyService,
                clock);
            using var settings = new TestSettingsStore();
            var taskId = new DownloadTaskId(downloadBase.Id);
            var stateWriter = new DownloadTaskStateWriter(tasks);
            await projectionStore.AddDownloadingAsync(
                downloading,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            await stateWriter.StartAsync(taskId, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            var stage = new ResolvePlaybackStage(
                new TestDesktopInteractionContext().Notifications,
                new DownloadActivityPresenter(projectionStore, stateWriter),
                new DownloadPlaybackResolver(
                    new TestWbiKeyProvider(),
                    TimeProvider.System,
                    new TestBilibiliApiClient()),
                NullLogger<ResolvePlaybackStage>.Instance);
            var context = new DownloadExecutionContextFactory(
                projectionStore,
                settings.Store).Create(taskId);

            var result = await stage.ExecuteAsync(
                context,
                TestContext.Current.CancellationToken);

            Assert.False(result.IsSuccess);
            Assert.Equal("download.resolve.recreate-task", result.Error?.Code);
            Assert.Null(context.DownloadDirectory);
        }
        finally
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = true,
                DefaultTimeout = 5
            }.ToString());
            SqliteConnection.ClearPool(connection);
            Directory.Delete(directory, recursive: true);
        }
    }
}
