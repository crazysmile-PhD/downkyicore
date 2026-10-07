using DownKyi.Application.Downloads;
using DownKyi.Core.BiliApi;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
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
    public async Task BangumiPlaybackStageReturnsSelectionUnavailableFailure()
    {
        var requestCount = 0;
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (_, _) =>
            {
                requestCount++;
                return Task.FromResult(requestCount == 1
                    ? """
                      {"code":0,"result":{"video_info":{"quality":112,"durl":[],"dash":{"video":[{"id":112,"codecid":13,"base_url":"https://api.invalid/video-112"}],"audio":[{"id":30280,"base_url":"https://api.invalid/audio"}]}}}}
                      """
                    : """
                      <script>const playurlSSRData = {"code":0,"result":{"video_info":{"quality":112,"durl":[],"dash":{"video":[{"id":112,"codecid":13,"base_url":"https://web.invalid/video-112"}],"audio":[{"id":30280,"base_url":"https://web.invalid/audio"}]}}}};</script>
                      """);
            }
        };
        using var fixture = await PlaybackStageFixture.CreateAsync(client).ConfigureAwait(true);

        var result = await fixture.Stage.ExecuteAsync(
            fixture.Context,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.playback.selection-unavailable", result.Error?.Code);
        Assert.Equal(OperationErrorKind.NotFound, result.Error?.Kind);
        Assert.Null(fixture.Context.PlayUrl);
        Assert.Equal(2, requestCount);
    }

    [Fact]
    public async Task BangumiPlaybackStageDoesNotMisclassifyOrdinaryApiFailure()
    {
        var expected = new BilibiliApiResponseException(
            "test-playback",
            "Synthetic malformed playback response.");
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (_, _) => Task.FromException<string>(expected)
        };
        using var fixture = await PlaybackStageFixture.CreateAsync(client).ConfigureAwait(true);

        var actual = await Assert.ThrowsAsync<BilibiliApiResponseException>(() =>
            fixture.Stage.ExecuteAsync(
                fixture.Context,
                TestContext.Current.CancellationToken));

        Assert.Same(expected, actual);
        Assert.Null(fixture.Context.PlayUrl);
    }

    [Fact]
    public async Task BangumiPlaybackStagePropagatesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = async (_, _) =>
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
                return await Task.FromCanceled<string>(cancellation.Token).ConfigureAwait(false);
            }
        };
        using var fixture = await PlaybackStageFixture.CreateAsync(client).ConfigureAwait(true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Stage.ExecuteAsync(fixture.Context, cancellation.Token));

        Assert.Null(fixture.Context.PlayUrl);
    }

    [Fact]
    public async Task RestartedBangumiTaskRestoresCompletedAudioBeforeVideoOnlyPlaybackResolve()
    {
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (_, _) => Task.FromResult(
                """
                {
                  "code": 0,
                  "result": {
                    "video_info": {
                      "quality": 112,
                      "durl": [],
                      "dash": {
                        "video": [
                          {
                            "id": 112,
                            "codecid": 12,
                            "codecs": "hev1",
                            "base_url": "https://media.invalid/restarted-video-112"
                          }
                        ],
                        "audio": []
                      }
                    }
                  }
                }
                """)
        };
        using var fixture = await PlaybackStageFixture.CreateAsync(client).ConfigureAwait(true);
        var completedAudio = await fixture.RecordCompletedAudioAsync().ConfigureAwait(true);
        await fixture.ReopenForRestartAsync(client).ConfigureAwait(true);

        var resolve = await fixture.Stage.ExecuteAsync(
            fixture.Context,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(resolve.IsSuccess, resolve.Error?.Message);
        Assert.NotEqual(completedAudio.FilePath, fixture.Context.AudioFile);
        Assert.Equal(completedAudio.Key, fixture.Context.AudioTransferKey);
        Assert.Equal(
            await File.ReadAllBytesAsync(
                completedAudio.FilePath,
                TestContext.Current.CancellationToken).ConfigureAwait(true),
            await File.ReadAllBytesAsync(
                fixture.Context.AudioFile!,
                TestContext.Current.CancellationToken).ConfigureAwait(true));

        var media = await fixture.MediaStage.ExecuteAsync(
            fixture.Context,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(media.IsSuccess, media.Error?.Message);
        var request = Assert.Single(fixture.Backend.Requests);
        Assert.Equal(
            "https://media.invalid/restarted-video-112",
            Assert.Single(request.Urls));
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

    private sealed class PlaybackStageFixture : IDisposable
    {
        private readonly string _directory;
        private readonly string _databasePath;
        private SqliteDownloadTaskStore _store;
        private DownloadTaskApplicationService _tasks;
        private DownloadTaskProjectionStore _projections;
        private readonly TestSettingsStore _settings;
        private readonly DownloadTaskId _taskId;
        private DownloadTaskStaging? _staging;

        private PlaybackStageFixture(
            string directory,
            string databasePath,
            SqliteDownloadTaskStore store,
            DownloadTaskApplicationService tasks,
            DownloadTaskProjectionStore projections,
            TestSettingsStore settings,
            DownloadTaskId taskId,
            ResolvePlaybackStage stage,
            DownloadExecutionContext context)
        {
            _directory = directory;
            _databasePath = databasePath;
            _store = store;
            _tasks = tasks;
            _projections = projections;
            _settings = settings;
            _taskId = taskId;
            Stage = stage;
            Context = context;
        }

        public ResolvePlaybackStage Stage { get; private set; }

        public DownloadExecutionContext Context { get; private set; }

        public DownloadMediaStage MediaStage { get; private set; } = null!;

        public RestartRecordingBackend Backend { get; private set; } = null!;

        public async Task<(string Key, string FilePath)> RecordCompletedAudioAsync()
        {
            const string fileName = "completed-audio.m4s";
            var filePath = Path.Combine(_directory, fileName);
            await File.WriteAllBytesAsync(
                filePath,
                [1, 2, 3],
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            var key = DownloadTransferKey.Create(30280, "mp4a.40.2");
            var writer = new DownloadTaskStateWriter(_tasks);
            await writer.RecordTransferFileAsync(
                _taskId,
                key,
                fileName,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            await writer.CompleteTransferFileAsync(
                _taskId,
                key,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            await writer.PauseAsync(
                _taskId,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            await writer.ConfirmPausedAsync(
                _taskId,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            return (key, filePath);
        }

        public async Task ReopenForRestartAsync(TestBilibiliApiClient client)
        {
            _projections.Dispose();
            _tasks.Dispose();
            _store.Dispose();

            _store = new SqliteDownloadTaskStore(
                new SqliteDownloadTaskStoreOptions(_databasePath),
                new SystemClock());
            var clock = new SystemClock();
            var historyService = DownloadHistoryService.CreateForSharedStore(_store);
            _tasks = new DownloadTaskApplicationService(_store, historyService, clock);
            _projections = new DownloadTaskProjectionStore(_tasks, historyService, clock);
            var startup = await _projections.GetDownloadingStateAsync(
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.Equal(_taskId, Assert.Single(startup.Tasks).Id);
            var writer = new DownloadTaskStateWriter(_tasks);
            await writer.ResumeAsync(
                _taskId,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            await writer.StartAsync(
                _taskId,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            _staging = new DownloadTaskStaging(
                NullLogger<DownloadTaskStaging>.Instance);
            Context = new DownloadExecutionContextFactory(
                _projections,
                _settings.Store,
                _staging).Create(_taskId);
            var resolver = new DownloadPlaybackResolver(
                new TestWbiKeyProvider(),
                TimeProvider.System,
                client);
            var presenter = new DownloadActivityPresenter(_projections, writer);
            Stage = new ResolvePlaybackStage(
                new TestDesktopInteractionContext().Notifications,
                presenter,
                resolver,
                NullLogger<ResolvePlaybackStage>.Instance);
            Backend = new RestartRecordingBackend();
            MediaStage = new DownloadMediaStage(
                _projections,
                writer,
                new DownloadTransferCoordinator(
                    Backend,
                    new DownloadRetryPolicy(),
                    TimeProvider.System,
                    NullLogger<DownloadTransferCoordinator>.Instance),
                resolver,
                presenter,
                NullLogger<DownloadMediaStage>.Instance);
        }

        public static async Task<PlaybackStageFixture> CreateAsync(TestBilibiliApiClient client)
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "downkyi-bangumi-playback-stage-tests",
                Guid.NewGuid().ToString("N"));
            var databasePath = Path.Combine(directory, "download.db");
            Directory.CreateDirectory(directory);
            var downloadBase = new DownloadBase
            {
                Id = $"bangumi-playback-{Guid.NewGuid():N}",
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
                AudioCodec = new DownKyi.Core.BiliApi.BiliUtils.Quality
                {
                    Id = 30280,
                    Name = "高质量"
                },
                NeedDownloadContent = DownloadContentSelection.None with
                {
                    Audio = true,
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
            var store = new SqliteDownloadTaskStore(
                new SqliteDownloadTaskStoreOptions(databasePath),
                new SystemClock());
            var clock = new SystemClock();
            var historyService = DownloadHistoryService.CreateForSharedStore(store);
            var tasks = new DownloadTaskApplicationService(store, historyService, clock);
            var projections = new DownloadTaskProjectionStore(tasks, historyService, clock);
            var settings = new TestSettingsStore();
            var taskId = new DownloadTaskId(downloadBase.Id);
            var stateWriter = new DownloadTaskStateWriter(tasks);
            await projections.AddDownloadingAsync(
                downloading,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            await stateWriter.StartAsync(taskId, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            var stage = new ResolvePlaybackStage(
                new TestDesktopInteractionContext().Notifications,
                new DownloadActivityPresenter(projections, stateWriter),
                new DownloadPlaybackResolver(
                    new TestWbiKeyProvider(),
                    TimeProvider.System,
                    client),
                NullLogger<ResolvePlaybackStage>.Instance);
            var context = new DownloadExecutionContextFactory(projections, settings.Store)
                .Create(taskId);
            return new PlaybackStageFixture(
                directory,
                databasePath,
                store,
                tasks,
                projections,
                settings,
                taskId,
                stage,
                context);
        }

        public void Dispose()
        {
            Backend?.Dispose();
            _staging?.CleanupCurrentSession();
            _projections.Dispose();
            _tasks.Dispose();
            _store.Dispose();
            _settings.Dispose();
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = true,
                DefaultTimeout = 5
            }.ToString());
            SqliteConnection.ClearPool(connection);
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class RestartRecordingBackend : ITransferBackend
    {
        public List<DownloadTransferRequest> Requests { get; } = [];

        public string Name => "restart-recording";

        public Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<DownloadTransferResult> ResetAsync(
            string? backendIdentity,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(DownloadTransferResult.Succeeded());
        }

        public async Task<DownloadTransferResult> TransferAsync(DownloadTransferRequest request)
        {
            Requests.Add(request);
            await File.WriteAllBytesAsync(
                Path.Combine(request.Directory, request.FileName),
                [4, 5, 6],
                request.CancellationToken).ConfigureAwait(true);
            return DownloadTransferResult.Succeeded();
        }

        public void Dispose()
        {
        }
    }
}
