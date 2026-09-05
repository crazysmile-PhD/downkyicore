using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Core.Settings;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
using DownKyi.Models;
using DownKyi.Services.Download;
using DownKyi.ViewModels.DownloadManager;

namespace DownKyi.Tests;

public sealed class DownloadPipelineStageTests
{
    [Fact]
    public void CreateAddressesTreatsExplicitNullBackupUrlsAsEmpty()
    {
        var media = new PlayUrlDashVideo
        {
            BaseAddress = "https://example.test/media.m4s",
            BackupUrl = null!
        };

        var addresses = DownloadMediaStage.CreateAddresses(media);

        Assert.Equal(["https://example.test/media.m4s"], addresses);
    }

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
    public async Task ValidateStageAllowsOptionalSubtitleResponseWithoutFiles()
    {
        using var settings = new TestSettingsStore();
        var context = CreateContext(settings.Store.Current);
        context.Downloading.DownloadBase.NeedDownloadContent["downloadAudio"] = false;
        context.Downloading.DownloadBase.NeedDownloadContent["downloadVideo"] = false;
        context.Downloading.DownloadBase.NeedDownloadContent["downloadDanmaku"] = false;
        context.Downloading.DownloadBase.NeedDownloadContent["downloadCover"] = false;
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
        var context = CreateContext(settings.Store.Current);
        context.Downloading.DownloadBase.Resolution.Id = 80;
        context.Downloading.DownloadBase.VideoCodecName = "H.264/AVC";
        var video = new PlayUrlDashVideo
        {
            Id = 80,
            CodecId = 7,
            Codecs = "avc1",
            ExpectedSize = 123_456
        };
        context.Downloading.PlayUrl = new PlayUrl
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

        Assert.Equal(
            DownloadMediaKind.Dash,
            DownloadMediaStage.DetectMediaKind(context.Downloading.PlayUrl));
        var selected = Assert.IsType<PlayUrlDashVideo>(
            DownloadMediaStage.SelectVideo(context));
        Assert.Same(video, selected);
        Assert.Equal(123_456, selected.ExpectedSize);
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

        var audioContext = CreateContext(settings.Store.Current);
        audioContext.Downloading.DownloadBase.NeedDownloadContent["downloadVideo"] = false;
        Assert.EndsWith(
            ".mp3",
            MuxStage.GetDashOutputPath(audioContext),
            StringComparison.Ordinal);

        var losslessContext = CreateContext(settings.Store.Current with
        {
            Video = settings.Store.Current.Video with
            {
                IsTranscodingAacToMp3 = AllowStatus.No
            }
        });
        losslessContext.Downloading.DownloadBase.AudioCodec.Id = 30251;
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

    private static DownloadExecutionContext CreateContext(ApplicationSettings settings)
    {
        var taskId = new DownloadTaskId("stage-test");
        var downloadBase = new DownloadBase
        {
            Id = taskId.Value,
            FilePath = Path.Combine(Path.GetTempPath(), "downkyi-stage-test")
        };
        var downloading = new DownloadingItem
        {
            DownloadBase = downloadBase,
            Downloading = new Downloading
            {
                Id = taskId.Value,
                DownloadBase = downloadBase,
                DownloadStatus = DownloadStatus.Downloading
            }
        };
        return new DownloadExecutionContext(
            taskId,
            downloading,
            settings,
            static (_, cancellationToken) =>
                cancellationToken.ThrowIfCancellationRequested());
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
