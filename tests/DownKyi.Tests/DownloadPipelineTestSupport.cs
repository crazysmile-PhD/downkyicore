using DownKyi.Application.Bilibili;
using DownKyi.Application.Downloads;
using DownKyi.Application.Settings;
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
using Newtonsoft.Json;

namespace DownKyi.Tests;

internal static class DownloadPipelineTestSupport
{
    internal static DownloadExecutionContext CreateContext(
        ApplicationSettings settings,
        DownloadContentSelection? requestedContent = null,
        int resolutionId = 0,
        string videoCodecName = "",
        int audioCodecId = 0,
        PlayUrl? playUrl = null,
        PlayStreamType streamType = PlayStreamType.None,
        string durationText = "")
    {
        var taskId = new DownloadTaskId("stage-test");
        var selection = requestedContent ?? DownloadContentSelection.All;
        if (selection.MediaKind is null)
        {
            var detectedKind = DownloadMediaContract.Detect(playUrl);
            selection = selection with
            {
                MediaKind = selection.Audio || selection.Video
                    ? detectedKind == DownloadMediaKind.None
                        ? DownloadMediaKind.Dash
                        : detectedKind
                    : DownloadMediaKind.None
            };
        }

        var downloadBase = new DownloadBase
        {
            Id = taskId.Value,
            FilePath = Path.Combine(Path.GetTempPath(), "downkyi-stage-test"),
            NeedDownloadContent = selection,
            Duration = durationText,
            VideoCodecName = videoCodecName
        };
        downloadBase.Resolution.Id = resolutionId;
        downloadBase.AudioCodec.Id = audioCodecId;
        var downloading = new DownloadingItem
        {
            DownloadBase = downloadBase,
            Downloading = new Downloading
            {
                Id = taskId.Value,
                DownloadBase = downloadBase,
                DownloadStatus = DownloadStatus.Downloading,
                PlayStreamType = streamType
            }
        };
        var context = DownloadExecutionContextTestFactory.Create(downloading, settings);
        context.PlayUrl = playUrl;
        return context;
    }

    internal static PlayUrl CreateVideoOnlyPlayUrl()
    {
        return new PlayUrl
        {
            Dash = new PlayUrlDash
            {
                Video =
                [
                    new PlayUrlDashVideo
                    {
                        Id = 80,
                        CodecId = 7,
                        Codecs = "avc1",
                        BaseAddress = "https://example.invalid/video"
                    }
                ]
            }
        };
    }

    internal static PlayUrl CreateAudioOnlyPlayUrl()
    {
        return new PlayUrl
        {
            Dash = new PlayUrlDash
            {
                Audio =
                [
                    new PlayUrlDashVideo
                    {
                        Id = 30280,
                        Codecs = "mp4a.40.2",
                        BaseAddress = "https://example.invalid/audio"
                    }
                ]
            }
        };
    }

    internal static PlayUrl CreateDurlPlayUrl(int quality = 80, int codecId = 7)
    {
        return new PlayUrl
        {
            Quality = quality,
            VideoCodecid = codecId,
            Durl =
            [
                new PlayUrlDurl
                {
                    Order = 1,
                    SourceAddress = "https://example.invalid/segment"
                }
            ]
        };
    }

    internal sealed class MediaStageFixture : IDisposable
    {
        private readonly string _directory;
        private readonly string _databasePath;
        private readonly SqliteDownloadTaskStore _store;
        private readonly DownloadTaskApplicationService _tasks;
        private readonly DownloadTaskProjectionStore _projections;
        private readonly TestSettingsStore _settings;

        private MediaStageFixture(
            string directory,
            string databasePath,
            SqliteDownloadTaskStore store,
            DownloadTaskApplicationService tasks,
            DownloadTaskProjectionStore projections,
            TestSettingsStore settings,
            RecordingMediaBackend backend,
            DownloadMediaStage stage,
            DownloadExecutionContext context,
            DownloadActivityPresenter presenter)
        {
            _directory = directory;
            _databasePath = databasePath;
            _store = store;
            _tasks = tasks;
            _projections = projections;
            _settings = settings;
            Backend = backend;
            Stage = stage;
            Context = context;
            Presenter = presenter;
        }

        public RecordingMediaBackend Backend { get; }

        public DownloadMediaStage Stage { get; }

        public DownloadExecutionContext Context { get; private set; }

        public DownloadActivityPresenter Presenter { get; }

        public Task<(string Key, string FilePath)> AddCompletedAudioTransferAsync() =>
            AddAudioTransferAsync(markCompleted: true, writeUsableFile: true);

        public Task<(string Key, string FilePath)> AddAudioTransferAsync(
            bool markCompleted,
            bool writeUsableFile) =>
            AddTransferAsync(
                id: 30280,
                codecs: "mp4a.40.2",
                fileName: "completed-audio.m4s",
                markCompleted,
                writeUsableFile);

        public Task<(string Key, string FilePath)> AddCompletedVideoTransferAsync() =>
            AddTransferAsync(
                id: 80,
                codecs: "avc1",
                fileName: "completed-video.m4s",
                markCompleted: true,
                writeUsableFile: true);

        public async Task<(string Key, string FilePath)> AddCompletedUnusableVideoTransferAsync()
        {
            var transfer = await AddTransferAsync(
                id: 80,
                codecs: "avc1",
                fileName: "invalid-completed-video.m4s",
                markCompleted: true,
                writeUsableFile: false).ConfigureAwait(true);
            await File.WriteAllBytesAsync(
                transfer.FilePath,
                [],
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            return transfer;
        }

        public async Task<DownloadTask> GetTaskAsync()
        {
            return await _tasks.FindAsync(
                       Context.TaskId,
                       TestContext.Current.CancellationToken).ConfigureAwait(true)
                   ?? throw new InvalidOperationException("Test task disappeared.");
        }

        private async Task<(string Key, string FilePath)> AddTransferAsync(
            int id,
            string codecs,
            string fileName,
            bool markCompleted,
            bool writeUsableFile)
        {
            var filePath = Path.Combine(_directory, fileName);
            if (writeUsableFile)
            {
                await File.WriteAllBytesAsync(
                    filePath,
                    [1, 2, 3],
                    TestContext.Current.CancellationToken).ConfigureAwait(true);
            }

            var key = DownloadTransferKey.Create(id, codecs);
            var recorded = await _tasks.RecordTransferFileAsync(
                Context.TaskId,
                key,
                fileName,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.True(recorded.IsSuccess, recorded.Error?.Message);
            if (markCompleted)
            {
                var completed = await _tasks.CompleteTransferFileAsync(
                    Context.TaskId,
                    key,
                    TestContext.Current.CancellationToken).ConfigureAwait(true);
                Assert.True(completed.IsSuccess, completed.Error?.Message);
            }

            var previous = Context;
            Context = new DownloadExecutionContextFactory(_tasks, _settings.Store)
                .Create(previous.TaskId);
            Context.PlayUrl = previous.PlayUrl;
            Context.DownloadDirectory = previous.DownloadDirectory;
            Context.StagingDirectory = previous.StagingDirectory;
            ResolvePlaybackStage.RestoreCompletedDashTransfers(Context);
            return (key, filePath);
        }

        public static async Task<MediaStageFixture> CreateAsync(
            PlayUrl playUrl,
            bool downloadAudio,
            bool downloadVideo,
            int selectedAudioId = 30280,
            TestBilibiliApiClient? apiClient = null,
            DownloadMediaKind? finalizedMediaKindOverride = null,
            PlayStreamType streamType = PlayStreamType.Video,
            params DownloadTransferResult[] backendResults)
        {
            if (playUrl.Durl.Count > 0)
            {
                playUrl.Quality = playUrl.Quality == 0 ? 80 : playUrl.Quality;
                playUrl.VideoCodecid = playUrl.VideoCodecid == 0 ? 7 : playUrl.VideoCodecid;
            }

            var directory = Path.Combine(
                Path.GetTempPath(),
                $"downkyi-media-stage-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var databasePath = Path.Combine(directory, "download.db");
            var store = new SqliteDownloadTaskStore(
                new SqliteDownloadTaskStoreOptions(databasePath),
                new SystemClock());
            var historyService = DownloadHistoryService.CreateForSharedStore(store);
            var tasks = new DownloadTaskApplicationService(store, historyService, new SystemClock());
            var projections = new DownloadTaskProjectionStore(
                tasks,
                historyService);
            var settings = new TestSettingsStore();
            var stateWriter = new DownloadTaskStateWriter(tasks);
            var taskId = new DownloadTaskId($"media-stage-{Guid.NewGuid():N}");
            var downloadBase = new DownloadBase
            {
                Id = taskId.Value,
                Bvid = "BV1fixture",
                Avid = 1,
                Cid = 2,
                EpisodeId = 3489,
                FilePath = Path.Combine(directory, "output"),
                NeedDownloadContent = new DownloadContentSelection(
                    Audio: downloadAudio,
                    Video: downloadVideo,
                    Danmaku: false,
                    Subtitle: false,
                    Cover: false)
                {
                    MediaKind = finalizedMediaKindOverride ??
                        (downloadAudio || downloadVideo
                            ? DownloadMediaContract.Detect(playUrl)
                            : DownloadMediaKind.None)
                },
                VideoCodecName = "H.264/AVC"
            };
            downloadBase.Resolution.Id = 80;
            downloadBase.AudioCodec.Id = selectedAudioId;
            var downloading = new DownloadingItem
            {
                DownloadBase = downloadBase,
                Downloading = new Downloading
                {
                    Id = taskId.Value,
                    DownloadBase = downloadBase,
                    PlayStreamType = streamType,
                    DownloadStatus = DownloadStatus.WaitForDownload
                }
            };
            await projections.AddDownloadingAsync(tasks,
                downloading,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            await stateWriter.StartAsync(taskId, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            downloading.Downloading.DownloadStatus = DownloadStatus.Downloading;
            var context = DownloadExecutionContextTestFactory.Create(
                downloading,
                settings.Store.Current);
            context.PlayUrl = playUrl;
            context.DownloadDirectory = directory;
            context.StagingDirectory = directory;
            var backend = new RecordingMediaBackend(backendResults);
            var presenter = new DownloadActivityPresenter(projections, stateWriter);
            var stage = new DownloadMediaStage(tasks,
                stateWriter,
                new DownloadTransferArtifactInvalidator(
                    stateWriter,
                    NullLogger<DownloadTransferArtifactInvalidator>.Instance),
                new DownloadTransferCoordinator(
                    backend,
                    new DownloadRetryPolicy(),
                    TimeProvider.System,
                    NullLogger<DownloadTransferCoordinator>.Instance),
                new DownloadPlaybackResolver(
                    new TestWbiKeyProvider(),
                    TimeProvider.System,
                    apiClient ?? new TestBilibiliApiClient()),
                presenter,
                NullLogger<DownloadMediaStage>.Instance);
            return new MediaStageFixture(
                directory,
                databasePath,
                store,
                tasks,
                projections,
                settings,
                backend,
                stage,
                context,
                presenter);
        }

        public void Dispose()
        {
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

    internal sealed class RecordingMediaBackend(
        params DownloadTransferResult[] results) : ITransferBackend
    {
        private readonly Queue<DownloadTransferResult> _results = new(results);

        public List<DownloadTransferRequest> Requests { get; } = [];

        public string Name => "recording-media";

        public async Task<DownloadTransferResult> TransferAsync(DownloadTransferRequest request)
        {
            Requests.Add(request);
            var result = _results.Count > 0
                ? _results.Dequeue()
                : DownloadTransferResult.Succeeded();
            if (result.Outcome != DownloadTransferOutcome.Succeeded)
            {
                return result;
            }

            await File.WriteAllBytesAsync(
                Path.Combine(request.Directory, request.FileName),
                [1, 2, 3],
                request.CancellationToken).ConfigureAwait(true);
            return result;
        }

    }

    internal sealed class RecordingStage(
        string name,
        ICollection<string> calls,
        bool succeed = true,
        CancellationToken? expectedToken = null) : IDownloadPipelineStage
    {
        public string Name { get; } = name;

        public Task<OperationResult<DownloadStageResult>> ExecuteAsync(
            DownloadExecutionContext context,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (expectedToken is { } token)
            {
                Assert.Equal(token, cancellationToken);
            }

            calls.Add(Name);
            return Task.FromResult(
                succeed
                    ? DownloadStageResult.Success(Name)
                    : DownloadStageResult.Failure(
                        "test.stage.failed",
                        "The test stage failed."));
        }
    }

    internal sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
