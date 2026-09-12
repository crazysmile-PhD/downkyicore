using DownKyi.Application.Downloads;
using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Core.Settings;
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

public sealed class DownloadPipelineStageTests
{
    [Fact]
    public async Task StageSequenceStopsAtFirstFailureAndPreservesOrder()
    {
        using var settings = new TestSettingsStore();
        var calls = new List<string>();
        using var cancellation = new CancellationTokenSource();
        var context = CreateContext(settings.Store.Current);
        IDownloadPipelineStage[] stages =
        [
            new RecordingStage("resolve", calls, succeed: true, cancellation.Token),
            new RecordingStage("media", calls, succeed: true, cancellation.Token),
            new RecordingStage("mux", calls, succeed: false, cancellation.Token),
            new RecordingStage("finalize", calls, succeed: true, cancellation.Token)
        ];

        var run = await DownloadPipeline.ExecuteStagesAsync(
            stages,
            context,
            cancellation.Token);

        Assert.False(run.Result.IsSuccess);
        Assert.Equal("mux", run.FailedStage);
        Assert.Equal("test.stage.failed", run.Result.Error?.Code);
        Assert.Equal(["resolve", "media", "mux"], calls);
    }

    [Fact]
    public async Task StageSequenceReturnsSuccessOnlyAfterEveryStageCompletes()
    {
        using var settings = new TestSettingsStore();
        var calls = new List<string>();
        var context = CreateContext(settings.Store.Current);
        IDownloadPipelineStage[] stages =
        [
            new RecordingStage("resolve", calls),
            new RecordingStage("media", calls),
            new RecordingStage("finalize", calls)
        ];

        var run = await DownloadPipeline.ExecuteStagesAsync(
            stages,
            context,
            TestContext.Current.CancellationToken);

        Assert.True(run.Result.IsSuccess);
        Assert.Null(run.FailedStage);
        Assert.Equal(["resolve", "media", "finalize"], calls);
    }

    [Fact]
    public async Task ValidateStageRejectsMissingRequestedMedia()
    {
        using var settings = new TestSettingsStore();
        var context = CreateContext(settings.Store.Current);
        context.MediaKind = DownloadMediaKind.Dash;
        context.MediaSucceeded = true;
        context.OutputMedia = Path.Combine(
            Path.GetTempPath(),
            $"missing-downkyi-media-{Guid.NewGuid():N}.mp4");

        var result = await new ValidateStage().ExecuteAsync(
            context,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.validate.media", result.Error?.Code);
    }

    [Fact]
    public async Task ValidateStageRejectsMissingRecordedPublishedMediaOnRetry()
    {
        using var settings = new TestSettingsStore();
        var context = CreateContext(settings.Store.Current);
        context.PublishedArtifacts.Add("media", Path.Combine(
            Path.GetTempPath(), $"missing-published-{Guid.NewGuid():N}.mp4"));

        var result = await new ValidateStage().ExecuteAsync(
            context, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.validate.media", result.Error?.Code);
    }

    [Fact]
    public async Task ValidateStageAllowsOptionalSubtitleResponseWithoutFiles()
    {
        using var settings = new TestSettingsStore();
        var context = CreateContext(
            settings.Store.Current,
            requestedContent: DownloadContentSelection.None with { Subtitle = true });
        context.SubtitleFiles = null;

        var result = await new ValidateStage().ExecuteAsync(
            context,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void MediaStageDetectsDurlWhenDashEnvelopeIsOnlyTheDefaultEmptyObject()
    {
        var playUrl = new PlayUrl
        {
            Durl =
            [
                new PlayUrlDurl
                {
                    Order = 1,
                    SourceAddress = "https://example.invalid/segment"
                }
            ]
        };

        Assert.Equal(DownloadMediaKind.Durl, DownloadMediaStage.DetectMediaKind(playUrl));
    }

    [Fact]
    public void MediaStagePrefersPopulatedDashAndPreservesExpectedSize()
    {
        using var settings = new TestSettingsStore();
        var video = new PlayUrlDashVideo
        {
            Id = 80,
            CodecId = 7,
            Codecs = "avc1",
            ExpectedSize = 123_456
        };
        var playUrl = new PlayUrl
        {
            Dash = new PlayUrlDash
            {
                Video = [video]
            },
            Durl =
            [
                new PlayUrlDurl
                {
                    Order = 1,
                    SourceAddress = "https://example.invalid/segment"
                }
            ]
        };
        var context = CreateContext(
            settings.Store.Current,
            resolutionId: 80,
            videoCodecName: "H.264/AVC",
            playUrl: playUrl);

        Assert.Equal(
            DownloadMediaKind.Dash,
            DownloadMediaStage.DetectMediaKind(context.PlayUrl));
        var selected = Assert.IsType<PlayUrlDashVideo>(
            DownloadMediaStage.SelectVideo(context));
        Assert.Same(video, selected);
        Assert.Equal(123_456, selected.ExpectedSize);
    }

    [Fact]
    public async Task MediaStageDownloadsVideoWhenRequestedAudioIsUnavailable()
    {
        using var fixture = await MediaStageFixture.CreateAsync(
            CreateVideoOnlyPlayUrl(),
            downloadAudio: true,
            downloadVideo: true).ConfigureAwait(true);

        var result = await fixture.Stage.ExecuteAsync(
            fixture.Context,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Null(fixture.Context.AudioFile);
        Assert.NotNull(fixture.Context.VideoFile);
        var request = Assert.Single(fixture.Backend.Requests);
        Assert.Equal("https://example.invalid/video", Assert.Single(request.Urls));
    }

    [Fact]
    public async Task MediaStageReusesCompletedStagingInsteadOfTransferringAgain()
    {
        using var fixture = await MediaStageFixture.CreateAsync(
            CreateVideoOnlyPlayUrl(), downloadAudio: false, downloadVideo: true);
        var staging = Path.Combine(Path.GetDirectoryName(fixture.Context.Input.OutputBasePath)!,
            ".downkyi", "staging", "test-session", "test-task");
        Directory.CreateDirectory(staging);
        fixture.Context.StagingDirectory = staging;
        var completedMedia = fixture.Context.WorkingBasePath + ".mp4";
        await File.WriteAllBytesAsync(completedMedia, [7, 8, 9], TestContext.Current.CancellationToken);
        fixture.Context.PlayUrl = null;

        var result = await fixture.Stage.ExecuteAsync(
            fixture.Context, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(completedMedia, fixture.Context.OutputMedia);
        Assert.Empty(fixture.Backend.Requests);
    }

    [Fact]
    public async Task MediaStageDownloadsVideoWhenAudioCollectionIsNull()
    {
        var playUrl = CreateVideoOnlyPlayUrl();
        playUrl.Dash.Audio = null!;
        using var fixture = await MediaStageFixture.CreateAsync(
            playUrl,
            downloadAudio: true,
            downloadVideo: true).ConfigureAwait(true);

        var result = await fixture.Stage.ExecuteAsync(
            fixture.Context,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Null(fixture.Context.AudioFile);
        Assert.NotNull(fixture.Context.VideoFile);
        var request = Assert.Single(fixture.Backend.Requests);
        Assert.Equal("https://example.invalid/video", Assert.Single(request.Urls));
    }

    [Fact]
    public async Task MediaStageReusesCompletedAudioWhenPlaybackNoLongerProvidesAudio()
    {
        using var fixture = await MediaStageFixture.CreateAsync(
            CreateVideoOnlyPlayUrl(),
            downloadAudio: true,
            downloadVideo: true).ConfigureAwait(true);
        var completedAudio = await fixture.AddCompletedAudioTransferAsync()
            .ConfigureAwait(true);

        var result = await fixture.Stage.ExecuteAsync(
            fixture.Context,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(completedAudio.FilePath, fixture.Context.AudioFile);
        Assert.Equal(completedAudio.Key, fixture.Context.AudioTransferKey);
        Assert.NotNull(fixture.Context.VideoFile);
        var request = Assert.Single(fixture.Backend.Requests);
        Assert.Equal("https://example.invalid/video", Assert.Single(request.Urls));
    }

    [Fact]
    public async Task MediaStageRejectsAudioOnlyRequestWhenSourceHasNoAudio()
    {
        using var fixture = await MediaStageFixture.CreateAsync(
            CreateVideoOnlyPlayUrl(),
            downloadAudio: true,
            downloadVideo: false).ConfigureAwait(true);

        var result = await fixture.Stage.ExecuteAsync(
            fixture.Context,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.media.descriptor", result.Error?.Code);
        Assert.Empty(fixture.Backend.Requests);
    }

    [Fact]
    public async Task MediaStageRejectsMissingSelectedAudioWhenSourceHasOtherAudio()
    {
        var playUrl = CreateVideoOnlyPlayUrl();
        playUrl.Dash.Audio =
        [
            new PlayUrlDashVideo
            {
                Id = 30280,
                Codecs = "mp4a.40.2",
                BaseAddress = "https://example.invalid/audio"
            }
        ];
        using var fixture = await MediaStageFixture.CreateAsync(
            playUrl,
            downloadAudio: true,
            downloadVideo: true,
            selectedAudioId: 30232).ConfigureAwait(true);

        var result = await fixture.Stage.ExecuteAsync(
            fixture.Context,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.media.descriptor", result.Error?.Code);
        Assert.Empty(fixture.Backend.Requests);
    }

    [Fact]
    public void MediaStageSelectsDolbyWhenOrdinaryAudioIsUnavailable()
    {
        using var settings = new TestSettingsStore();
        var dolby = new PlayUrlDashVideo
        {
            Id = 30250,
            BaseAddress = "https://example.invalid/dolby"
        };
        var context = CreateContext(
            settings.Store.Current,
            audioCodecId: 30250,
            playUrl: new PlayUrl
            {
                Dash = new PlayUrlDash
                {
                    Dolby = new PlayUrlDashDolby { Audio = [dolby] }
                }
            });

        Assert.Same(dolby, DownloadMediaStage.SelectAudio(context));
    }

    [Fact]
    public void MediaStageSelectsFlacWhenOrdinaryAudioIsUnavailable()
    {
        using var settings = new TestSettingsStore();
        var flac = new PlayUrlDashVideo
        {
            Id = 30251,
            BaseAddress = "https://example.invalid/flac"
        };
        var context = CreateContext(
            settings.Store.Current,
            audioCodecId: 30251,
            playUrl: new PlayUrl
            {
                Dash = new PlayUrlDash
                {
                    Flac = new PlayUrlDashFlac { Audio = flac }
                }
            });

        Assert.Same(flac, DownloadMediaStage.SelectAudio(context));
    }

    [Theory]
    [InlineData(30250)]
    [InlineData(30251)]
    public void MediaStageFallsBackToOrdinaryAudioWhenSpecialDescriptorIsUnavailable(
        int audioCodecId)
    {
        using var settings = new TestSettingsStore();
        var ordinary = new PlayUrlDashVideo
        {
            Id = audioCodecId,
            BaseAddress = "https://example.invalid/ordinary-audio"
        };
        var context = CreateContext(
            settings.Store.Current,
            audioCodecId: audioCodecId,
            playUrl: new PlayUrl
            {
                Dash = new PlayUrlDash { Audio = [ordinary] }
            });

        Assert.Same(ordinary, DownloadMediaStage.SelectAudio(context));
    }

    [Fact]
    public void MuxStageSelectsOutputFromRequestedStreamShape()
    {
        using var settings = new TestSettingsStore();
        var videoContext = CreateContext(settings.Store.Current);
        videoContext.VideoFile = "video-stream";
        Assert.EndsWith(
            ".mp4",
            MuxStage.GetDashOutputPath(videoContext),
            StringComparison.Ordinal);

        var audioContext = CreateContext(
            settings.Store.Current,
            requestedContent: DownloadContentSelection.None with { Audio = true });
        Assert.EndsWith(
            ".mp3",
            MuxStage.GetDashOutputPath(audioContext),
            StringComparison.Ordinal);

        var losslessContext = CreateContext(
            settings.Store.Current with
            {
                Video = settings.Store.Current.Video with
                {
                    IsTranscodingAacToMp3 = AllowStatus.No
                }
            },
            audioCodecId: 30251);
        Assert.EndsWith(
            ".flac",
            MuxStage.GetDashOutputPath(losslessContext),
            StringComparison.Ordinal);
    }

    [Fact]
    public void FinalizeStageUsesInjectedClockForCompletionSummary()
    {
        var finishedAt = new DateTimeOffset(
            2026,
            7,
            26,
            1,
            2,
            3,
            TimeSpan.Zero);

        var downloaded = FinalizeStage.CreateDownloadedSummary(
            maximumBytesPerSecond: 1_250_000,
            timeProvider: new FixedTimeProvider(finishedAt));

        Assert.Equal(finishedAt.ToUnixTimeSeconds(), downloaded.FinishedTimestamp);
        Assert.False(string.IsNullOrEmpty(downloaded.FinishedTime));
        Assert.False(string.IsNullOrEmpty(downloaded.MaxSpeedDisplay));
    }

    private static DownloadExecutionContext CreateContext(
        ApplicationSettings settings,
        DownloadContentSelection? requestedContent = null,
        int resolutionId = 0,
        string videoCodecName = "",
        int audioCodecId = 0,
        PlayUrl? playUrl = null)
    {
        var taskId = new DownloadTaskId("stage-test");
        var downloadBase = new DownloadBase
        {
            Id = taskId.Value,
            FilePath = Path.Combine(Path.GetTempPath(), "downkyi-stage-test"),
            NeedDownloadContent = requestedContent ?? DownloadContentSelection.All,
            VideoCodecName = videoCodecName
        };
        downloadBase.Resolution.Id = resolutionId;
        downloadBase.AudioCodec.Id = audioCodecId;
        var downloading = new DownloadingItem
        {
            DownloadBase = downloadBase,
            PlayUrl = playUrl!,
            Downloading = new Downloading
            {
                Id = taskId.Value,
                DownloadBase = downloadBase,
                DownloadStatus = DownloadStatus.Downloading
            }
        };
        return DownloadExecutionContextTestFactory.Create(downloading, settings);
    }

    private static PlayUrl CreateVideoOnlyPlayUrl()
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

    private sealed class MediaStageFixture : IDisposable
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
            DownloadExecutionContext context)
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
        }

        public RecordingMediaBackend Backend { get; }

        public DownloadMediaStage Stage { get; }

        public DownloadExecutionContext Context { get; }

        public async Task<(string Key, string FilePath)> AddCompletedAudioTransferAsync()
        {
            const string fileName = "completed-audio.m4s";
            var filePath = Path.Combine(_directory, fileName);
            await File.WriteAllBytesAsync(
                filePath,
                [1, 2, 3],
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            var key = DownloadTransferKey.Create(30280, "mp4a.40.2");
            var recorded = await _tasks.RecordTransferFileAsync(
                Context.TaskId,
                key,
                fileName,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.True(recorded.IsSuccess, recorded.Error?.Message);
            var completed = await _tasks.CompleteTransferFileAsync(
                Context.TaskId,
                key,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.True(completed.IsSuccess, completed.Error?.Message);
            return (key, filePath);
        }

        public static async Task<MediaStageFixture> CreateAsync(
            PlayUrl playUrl,
            bool downloadAudio,
            bool downloadVideo,
            int selectedAudioId = 30280)
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                $"downkyi-media-stage-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var databasePath = Path.Combine(directory, "download.db");
            var store = new SqliteDownloadTaskStore(
                new SqliteDownloadTaskStoreOptions(databasePath),
                new SystemClock());
            var tasks = new DownloadTaskApplicationService(store, new SystemClock());
            var projections = new DownloadTaskProjectionStore(tasks, new SystemClock());
            var settings = new TestSettingsStore();
            var stateWriter = new DownloadTaskStateWriter(tasks);
            var taskId = new DownloadTaskId($"media-stage-{Guid.NewGuid():N}");
            var downloadBase = new DownloadBase
            {
                Id = taskId.Value,
                FilePath = Path.Combine(directory, "output"),
                NeedDownloadContent = new DownloadContentSelection(
                    Audio: downloadAudio,
                    Video: downloadVideo,
                    Danmaku: false,
                    Subtitle: false,
                    Cover: false),
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
                    DownloadStatus = DownloadStatus.WaitForDownload
                },
                PlayUrl = playUrl
            };
            await projections.AddDownloadingAsync(
                downloading,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            await stateWriter.StartAsync(taskId, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            downloading.Downloading.DownloadStatus = DownloadStatus.Downloading;
            var context = DownloadExecutionContextTestFactory.Create(
                downloading,
                settings.Store.Current);
            context.DownloadDirectory = directory;
            var backend = new RecordingMediaBackend();
            var stage = new DownloadMediaStage(
                projections,
                stateWriter,
                new DownloadTransferCoordinator(
                    backend,
                    new DownloadRetryPolicy(),
                    TimeProvider.System,
                    NullLogger<DownloadTransferCoordinator>.Instance),
                new DownloadPlaybackResolver(
                    new TestWbiKeyProvider(),
                    TimeProvider.System,
                    new TestBilibiliApiClient()),
                new DownloadActivityPresenter(projections, stateWriter),
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
                context);
        }

        public void Dispose()
        {
            Backend.Dispose();
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

    private sealed class RecordingMediaBackend : ITransferBackend
    {
        public List<DownloadTransferRequest> Requests { get; } = [];

        public string Name => "recording-media";

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
                [1, 2, 3],
                request.CancellationToken).ConfigureAwait(true);
            return DownloadTransferResult.Succeeded();
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingStage(
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

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
