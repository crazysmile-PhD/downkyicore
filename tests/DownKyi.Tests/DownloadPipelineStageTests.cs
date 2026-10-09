using DownKyi.Application.Bilibili;
using DownKyi.Application.Downloads;
using DownKyi.Core.BiliApi.VideoStream;
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
using Newtonsoft.Json;

namespace DownKyi.Tests;

public sealed class DownloadPipelineStageTests
{
    [Fact]
    public async Task AudioOnlyRefreshProbesWithValidQualityInsteadOfZero()
    {
        using var settings = new TestSettingsStore();
        string? requestedAddress = null;
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (request, _) =>
            {
                requestedAddress = request.RequestAddress;
                return Task.FromResult(
                    """
                    {"code":0,"data":{"dash":{"audio":[{"id":30280,"base_url":"https://media.invalid/audio.m4s"}]}}}
                    """);
            }
        };
        var context = CreateContext(
            settings.Store.Current,
            requestedContent: DownloadContentSelection.None with { Audio = true },
            audioCodecId: 30280,
            streamType: PlayStreamType.Video);
        var resolver = new DownloadPlaybackResolver(
            new TestWbiKeyProvider(),
            TimeProvider.System,
            client);

        var result = await resolver.ResolveAsync(
            context,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Contains("qn=127", requestedAddress, StringComparison.Ordinal);
        Assert.DoesNotContain("qn=0", requestedAddress, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrdinaryResumeUsesFinalizedCodecWhenWebPageHasAnotherCodec()
    {
        using var settings = new TestSettingsStore();
        settings.Store.Update(current => current with
        {
            Video = current.Video with { VideoParseType = 1 }
        });
        var requests = new List<string>();
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (request, _) =>
            {
                requests.Add(request.RequestAddress);
                return Task.FromResult(request.RequestAddress.StartsWith(
                    "https://www.bilibili.com/video/",
                    StringComparison.Ordinal)
                    ? """
                      <script>window.__playinfo__={"code":0,"data":{"dash":{"video":[{"id":80,"codecid":12,"base_url":"https://web.invalid/hevc"}],"audio":[]}}}</script>
                      """
                    : """
                      {"code":0,"data":{"dash":{"video":[{"id":80,"codecid":7,"base_url":"https://api.invalid/avc"}],"audio":[]}}}
                      """);
            }
        };
        var context = CreateContext(
            settings.Store.Current,
            requestedContent: DownloadContentSelection.None with { Video = true },
            resolutionId: 80,
            videoCodecName: "H.264/AVC",
            streamType: PlayStreamType.Video);
        var resolver = new DownloadPlaybackResolver(
            new TestWbiKeyProvider(),
            TimeProvider.System,
            client);

        var result = await resolver.ResolveAsync(context, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(result.TryGetValue(out var playback));
        Assert.Equal("https://api.invalid/avc", Assert.Single(playback.Dash.Video).BaseAddress);
        Assert.Equal(2, requests.Count);
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
    public void SelectionUnavailableFailureRemainsExplicitAndNonTransient()
    {
        const string message =
            "The selected playback quality, codec, or audio is no longer available.";
        var error = new OperationError(
            "download.playback.selection-unavailable",
            message,
            OperationErrorKind.NotFound);

        var failure = DownloadActivityPresenter.CreateFailure(error);

        Assert.Equal("download.playback.selection-unavailable", failure.Code);
        Assert.NotEqual("download.runtime.failed", failure.Code);
        Assert.Equal(message, failure.Message);
        Assert.False(failure.IsTransient);
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

        var result = await new ValidateStage(new StubFfmpegMediaStreamValidator()).ExecuteAsync(
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

        var result = await new ValidateStage(new StubFfmpegMediaStreamValidator()).ExecuteAsync(
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

        var result = await new ValidateStage(new StubFfmpegMediaStreamValidator()).ExecuteAsync(
            context,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
    }

    [Theory]
    [InlineData("subtitle:missing.srt")]
    [InlineData("cover")]
    [InlineData("nfo")]
    public async Task ValidateStageRejectsMissingRecordedArtifactEvenWhenNewResponseIsEmpty(string key)
    {
        using var settings = new TestSettingsStore();
        var context = CreateContext(
            settings.Store.Current,
            requestedContent: DownloadContentSelection.None with { Subtitle = true });
        context.PublishedArtifacts[key] = Path.Combine(
            Path.GetTempPath(), $"missing-published-{Guid.NewGuid():N}.srt");
        context.SubtitleFiles = null;

        var result = await new ValidateStage(new StubFfmpegMediaStreamValidator()).ExecuteAsync(
            context, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.validate.published-missing", result.Error?.Code);
    }

    [Theory]
    [InlineData(true, true, ".mp4", false, false)]
    [InlineData(true, true, ".mp4", true, true)]
    [InlineData(false, true, ".mp4", true, true)]
    [InlineData(true, false, ".mp3", true, true)]
    [InlineData(true, false, ".aac", true, true)]
    [InlineData(true, false, ".flac", true, true)]
    public async Task ValidateStageAppliesRequestedStreamShapeToReusedStagedMedia(
        bool requestAudio,
        bool requestVideo,
        string extension,
        bool canDecodeRequiredStreams,
        bool expectedSuccess)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-stream-shape-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var settings = new TestSettingsStore();
            var context = CreateContext(
                settings.Store.Current,
                DownloadContentSelection.None with
                {
                    Audio = requestAudio,
                    Video = requestVideo
                });
            context.StagingDirectory = directory;
            var stagedMedia = context.WorkingBasePath + extension;
            await File.WriteAllBytesAsync(
                stagedMedia,
                [1, 2, 3],
                TestContext.Current.CancellationToken);
            Assert.True(context.TryReuseStagedMedia());
            var validator = new StubFfmpegMediaStreamValidator(canDecodeRequiredStreams);

            var result = await new ValidateStage(validator).ExecuteAsync(
                context,
                TestContext.Current.CancellationToken);

            Assert.Equal(expectedSuccess, result.IsSuccess);
            if (!expectedSuccess)
            {
                Assert.Equal("download.validate.media", result.Error?.Code);
            }

            var call = Assert.Single(validator.Calls);
            Assert.Equal(stagedMedia, call.MediaFile);
            Assert.Equal(requestAudio, call.RequireAudio);
            Assert.Equal(requestVideo, call.RequireVideo);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ValidateStageDoesNotRepeatMultiSegmentDurlVideoDecode()
    {
        var mediaFile = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-validated-durl-{Guid.NewGuid():N}.mp4");
        try
        {
            await File.WriteAllBytesAsync(
                mediaFile,
                [1, 2, 3],
                TestContext.Current.CancellationToken);
            using var settings = new TestSettingsStore();
            var context = CreateContext(
                settings.Store.Current,
                DownloadContentSelection.None with
                {
                    Video = true,
                    MediaKind = DownloadMediaKind.Durl
                });
            context.MediaKind = DownloadMediaKind.Durl;
            context.OutputMedia = mediaFile;
            context.MediaSucceeded = true;
            context.DurlDownloads =
            [
                new DurlDownloadResult(new PlayUrlDurl { Order = 1 }, "segment-1", "key-1"),
                new DurlDownloadResult(new PlayUrlDurl { Order = 2 }, "segment-2", "key-2")
            ];
            var validator = new StubFfmpegMediaStreamValidator(result: false);

            var result = await new ValidateStage(validator).ExecuteAsync(
                context,
                TestContext.Current.CancellationToken);

            Assert.True(result.IsSuccess);
            Assert.Empty(validator.Calls);
        }
        finally
        {
            File.Delete(mediaFile);
        }
    }

    [Fact]
    public async Task ValidateStageOnlyDecodesAudioAfterMultiSegmentDurlVideoValidation()
    {
        var mediaFile = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-validated-durl-audio-{Guid.NewGuid():N}.mp4");
        try
        {
            await File.WriteAllBytesAsync(
                mediaFile,
                [1, 2, 3],
                TestContext.Current.CancellationToken);
            using var settings = new TestSettingsStore();
            var context = CreateContext(
                settings.Store.Current,
                DownloadContentSelection.None with
                {
                    Audio = true,
                    Video = true,
                    MediaKind = DownloadMediaKind.Durl
                });
            context.MediaKind = DownloadMediaKind.Durl;
            context.OutputMedia = mediaFile;
            context.MediaSucceeded = true;
            context.DurlDownloads =
            [
                new DurlDownloadResult(new PlayUrlDurl { Order = 1 }, "segment-1", "key-1"),
                new DurlDownloadResult(new PlayUrlDurl { Order = 2 }, "segment-2", "key-2")
            ];
            var validator = new StubFfmpegMediaStreamValidator();

            var result = await new ValidateStage(validator).ExecuteAsync(
                context,
                TestContext.Current.CancellationToken);

            Assert.True(result.IsSuccess);
            var call = Assert.Single(validator.Calls);
            Assert.True(call.RequireAudio);
            Assert.False(call.RequireVideo);
        }
        finally
        {
            File.Delete(mediaFile);
        }
    }

    [Fact]
    public async Task ValidateStageRequiresDecodeEvidenceForReusedDurlMedia()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-reused-durl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var settings = new TestSettingsStore();
            var context = CreateContext(
                settings.Store.Current,
                DownloadContentSelection.None with
                {
                    Video = true,
                    MediaKind = DownloadMediaKind.Durl
                });
            context.MediaKind = DownloadMediaKind.Durl;
            context.StagingDirectory = directory;
            var stagedMedia = context.WorkingBasePath + ".mp4";
            await File.WriteAllBytesAsync(
                stagedMedia,
                [1, 2, 3],
                TestContext.Current.CancellationToken);
            Assert.True(context.TryReuseStagedMedia());
            var validator = new StubFfmpegMediaStreamValidator(result: false);

            var result = await new ValidateStage(validator).ExecuteAsync(
                context,
                TestContext.Current.CancellationToken);

            Assert.False(result.IsSuccess);
            var call = Assert.Single(validator.Calls);
            Assert.False(call.RequireAudio);
            Assert.True(call.RequireVideo);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ValidateStageRequiresDecodeEvidenceForPublishedMedia()
    {
        var mediaFile = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-published-media-{Guid.NewGuid():N}.mp3");
        try
        {
            await File.WriteAllBytesAsync(
                mediaFile,
                [1, 2, 3],
                TestContext.Current.CancellationToken);
            using var settings = new TestSettingsStore();
            var context = CreateContext(
                settings.Store.Current,
                DownloadContentSelection.None with { Audio = true });
            context.PublishedArtifacts["media"] = mediaFile;
            var validator = new StubFfmpegMediaStreamValidator(result: false);

            var result = await new ValidateStage(validator).ExecuteAsync(
                context,
                TestContext.Current.CancellationToken);

            Assert.False(result.IsSuccess);
            var call = Assert.Single(validator.Calls);
            Assert.Equal(mediaFile, call.MediaFile);
            Assert.True(call.RequireAudio);
            Assert.False(call.RequireVideo);
        }
        finally
        {
            File.Delete(mediaFile);
        }
    }

    [Fact]
    public void MediaStageDetectsDurlWhenDashEnvelopeIsOnlyTheDefaultEmptyObject()
    {
        var playUrl = new PlayUrl
        {
            Quality = 80,
            VideoCodecid = 7,
            Durl =
            [
                new PlayUrlDurl
                {
                    Order = 1,
                    SourceAddress = "https://example.invalid/segment"
                }
            ]
        };

        Assert.Equal(DownloadMediaKind.Durl, DownloadMediaContract.Detect(playUrl));
    }

    [Fact]
    public void MediaContractValidatesFinalizedTransportWhenOneSourceContainsDashAndDurl()
    {
        var playUrl = CreateDurlPlayUrl();
        playUrl.Dash.Video =
        [
            new PlayUrlDashVideo
            {
                Id = 80,
                CodecId = 7,
                BaseAddress = "https://media.invalid/video-80.m4s"
            }
        ];
        using var settings = new TestSettingsStore();
        var dashContext = CreateContext(
            settings.Store.Current,
            DownloadContentSelection.None with
            {
                Video = true,
                MediaKind = DownloadMediaKind.Dash
            },
            resolutionId: 80,
            videoCodecName: "H.264/AVC",
            playUrl: playUrl);
        var durlContext = CreateContext(
            settings.Store.Current,
            DownloadContentSelection.None with
            {
                Video = true,
                MediaKind = DownloadMediaKind.Durl
            },
            resolutionId: 80,
            videoCodecName: "H.264/AVC",
            playUrl: playUrl);

        Assert.Null(DownloadMediaContract.Validate(dashContext, playUrl));
        Assert.Null(DownloadMediaContract.Validate(durlContext, playUrl));
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
            ExpectedSize = 123_456,
            BaseAddress = "https://example.invalid/video"
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
            DownloadMediaContract.Detect(context.PlayUrl));
        var selected = Assert.IsType<PlayUrlDashVideo>(
            DownloadMediaStage.SelectVideo(context));
        Assert.Same(video, selected);
        Assert.Equal(123_456, selected.ExpectedSize);
    }

    [Fact]
    public async Task MediaStageRejectsVideoAndAudioRequestWhenAudioIsUnavailable()
    {
        using var fixture = await MediaStageFixture.CreateAsync(
            CreateVideoOnlyPlayUrl(),
            downloadAudio: true,
            downloadVideo: true).ConfigureAwait(true);

        var result = await fixture.Stage.ExecuteAsync(
            fixture.Context,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.playback.selection-unavailable", result.Error?.Code);
        Assert.Null(fixture.Context.AudioFile);
        Assert.Null(fixture.Context.VideoFile);
        Assert.Empty(fixture.Backend.Requests);
    }

    [Fact]
    public async Task MediaStageDoesNotInferMediaWhenFinalizedSelectionRequestsNone()
    {
        using var fixture = await MediaStageFixture.CreateAsync(
            CreateVideoOnlyPlayUrl(),
            downloadAudio: false,
            downloadVideo: false).ConfigureAwait(true);

        var result = await fixture.Stage.ExecuteAsync(
            fixture.Context,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(DownloadMediaKind.None, fixture.Context.MediaKind);
        Assert.Empty(fixture.Backend.Requests);
    }

    [Fact]
    public async Task MediaStageDoesNotAddRetryAfterCoordinatorStops()
    {
        using var fixture = await MediaStageFixture.CreateAsync(
            CreateVideoOnlyPlayUrl(),
            downloadAudio: false,
            downloadVideo: true,
            backendResults:
            [
                DownloadTransferResult.Failed(
                    DownloadTransferFailureKind.Permanent,
                    "download.transfer.permanent")
            ]).ConfigureAwait(true);

        var result = await fixture.Stage.ExecuteAsync(
            fixture.Context,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.transfer.permanent", result.Error?.Code);
        Assert.Single(fixture.Backend.Requests);
    }

    [Fact]
    public async Task MediaStageUsesBaseAddressWhenDashBackupUrlsAreNull()
    {
        var playUrl = JsonConvert.DeserializeObject<PlayUrl>("""
            {
              "dash": {
                "video": [{
                  "id": 80,
                  "codecid": 7,
                  "codecs": "avc1",
                  "base_url": "https://example.invalid/video",
                  "backup_url": null
                }]
              }
            }
            """);
        Assert.NotNull(playUrl);
        using var fixture = await MediaStageFixture.CreateAsync(
            playUrl,
            downloadAudio: false,
            downloadVideo: true).ConfigureAwait(true);

        var result = await fixture.Stage.ExecuteAsync(
            fixture.Context,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var request = Assert.Single(fixture.Backend.Requests);
        Assert.Equal("https://example.invalid/video", Assert.Single(request.Urls));
    }

    [Fact]
    public async Task MediaStageRejectsDuplicateDurlOrdersBeforeAnyTransfer()
    {
        var playUrl = new PlayUrl
        {
            Durl =
            [
                new PlayUrlDurl
                {
                    Order = 7,
                    SourceAddress = "https://example.invalid/segment-7-primary"
                },
                new PlayUrlDurl
                {
                    Order = 7,
                    SourceAddress = "https://example.invalid/segment-7-duplicate"
                }
            ]
        };
        using var fixture = await MediaStageFixture.CreateAsync(
            playUrl,
            downloadAudio: false,
            downloadVideo: true,
            finalizedMediaKindOverride: DownloadMediaKind.Durl).ConfigureAwait(true);

        var result = await fixture.Stage.ExecuteAsync(
            fixture.Context,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.playback.selection-unavailable", result.Error?.Code);
        Assert.Empty(fixture.Backend.Requests);
    }

    [Fact]
    public async Task MediaStageRejectsAddresslessDurlBeforeEarlierSegmentTransfer()
    {
        var playUrl = new PlayUrl
        {
            Durl =
            [
                new PlayUrlDurl
                {
                    Order = 1,
                    SourceAddress = "https://example.invalid/segment-1"
                },
                new PlayUrlDurl
                {
                    Order = 2,
                    SourceAddress = "  ",
                    BackupUrl = ["", "  "]
                }
            ]
        };
        using var fixture = await MediaStageFixture.CreateAsync(
            playUrl,
            downloadAudio: false,
            downloadVideo: true,
            finalizedMediaKindOverride: DownloadMediaKind.Durl).ConfigureAwait(true);

        var result = await fixture.Stage.ExecuteAsync(
            fixture.Context,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.playback.selection-unavailable", result.Error?.Code);
        Assert.Empty(fixture.Backend.Requests);
    }

    [Fact]
    public async Task MediaStageTransfersValidatedDurlsInOrder()
    {
        var playUrl = new PlayUrl
        {
            Durl =
            [
                new PlayUrlDurl
                {
                    Order = 9,
                    SourceAddress = "https://example.invalid/segment-9"
                },
                new PlayUrlDurl
                {
                    Order = 2,
                    SourceAddress = "https://example.invalid/segment-2"
                },
                new PlayUrlDurl
                {
                    Order = 5,
                    SourceAddress = "",
                    BackupUrl = ["https://example.invalid/segment-5"]
                }
            ]
        };
        using var fixture = await MediaStageFixture.CreateAsync(
            playUrl,
            downloadAudio: false,
            downloadVideo: true).ConfigureAwait(true);

        var result = await fixture.Stage.ExecuteAsync(
            fixture.Context,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            [
                "https://example.invalid/segment-2",
                "https://example.invalid/segment-5",
                "https://example.invalid/segment-9"
            ],
            fixture.Backend.Requests.Select(request => Assert.Single(request.Urls)));
    }

    [Fact]
    public async Task MediaStageRejectsMalformedRefreshedDurlManifestBeforeRetryTransfer()
    {
        var playUrl = new PlayUrl
        {
            Durl =
            [
                new PlayUrlDurl
                {
                    Order = 1,
                    SourceAddress = "https://example.invalid/original-segment"
                }
            ]
        };
        var refreshRequestCount = 0;
        var apiClient = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (_, _) =>
            {
                refreshRequestCount++;
                return Task.FromResult(
                    """
                    {
                      "code": 0,
                      "message": "success",
                      "data": {
                        "quality": 80,
                        "video_codecid": 7,
                        "durl": [
                          { "order": 1, "url": "https://example.invalid/refreshed-a" },
                          { "order": 1, "url": "https://example.invalid/refreshed-b" }
                        ]
                      }
                    }
                    """);
            }
        };
        using var fixture = await MediaStageFixture.CreateAsync(
            playUrl,
            downloadAudio: false,
            downloadVideo: true,
            apiClient: apiClient,
            backendResults:
            [
                DownloadTransferResult.Failed(
                    DownloadTransferFailureKind.ExpiredAddress,
                    "download.transfer.http-403")
            ]).ConfigureAwait(true);

        var result = await fixture.Stage.ExecuteAsync(
            fixture.Context,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.playback.selection-unavailable", result.Error?.Code);
        Assert.Equal(1, refreshRequestCount);
        Assert.Single(fixture.Backend.Requests);
    }

    [Fact]
    public async Task MediaStageRejectsAudioOnlyDurlWithoutStartingTransfer()
    {
        using var fixture = await MediaStageFixture.CreateAsync(
            CreateDurlPlayUrl(),
            downloadAudio: true,
            downloadVideo: false).ConfigureAwait(true);

        var result = await fixture.Stage.ExecuteAsync(
            fixture.Context,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.media.durl-audio-only", result.Error?.Code);
        Assert.Empty(fixture.Backend.Requests);
    }

    [Theory]
    [InlineData(64, 7)]
    [InlineData(80, 12)]
    public async Task MediaStageRejectsDurlThatDoesNotMatchFinalizedVideoSelection(
        int quality,
        int codecId)
    {
        using var fixture = await MediaStageFixture.CreateAsync(
            CreateDurlPlayUrl(quality, codecId),
            downloadAudio: false,
            downloadVideo: true).ConfigureAwait(true);

        var result = await fixture.Stage.ExecuteAsync(
            fixture.Context,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.playback.selection-unavailable", result.Error?.Code);
        Assert.Empty(fixture.Backend.Requests);
    }

    [Fact]
    public async Task MediaStageRejectsPlaybackTransportThatDiffersFromFinalizedContract()
    {
        using var fixture = await MediaStageFixture.CreateAsync(
            CreateDurlPlayUrl(),
            downloadAudio: false,
            downloadVideo: true,
            finalizedMediaKindOverride: DownloadMediaKind.Dash).ConfigureAwait(true);

        var result = await fixture.Stage.ExecuteAsync(
            fixture.Context,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.playback.selection-unavailable", result.Error?.Code);
        Assert.Empty(fixture.Backend.Requests);
    }

    [Fact]
    public async Task MediaStageRejectsRefreshedDurlThatChangesFinalizedQuality()
    {
        BilibiliHttpRequest? refreshRequest = null;
        var apiClient = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (request, _) =>
            {
                refreshRequest = request;
                return Task.FromResult(
                    """
                    {
                      "code": 0,
                      "message": "success",
                      "data": {
                        "quality": 64,
                        "video_codecid": 7,
                        "durl": [
                          { "order": 1, "url": "https://example.invalid/refreshed-segment" }
                        ]
                      }
                    }
                    """);
            }
        };
        using var fixture = await MediaStageFixture.CreateAsync(
            CreateDurlPlayUrl(),
            downloadAudio: false,
            downloadVideo: true,
            apiClient: apiClient,
            backendResults:
            [
                DownloadTransferResult.Failed(
                    DownloadTransferFailureKind.ExpiredAddress,
                    "download.transfer.http-403")
            ]).ConfigureAwait(true);

        var result = await fixture.Stage.ExecuteAsync(
            fixture.Context,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.playback.selection-unavailable", result.Error?.Code);
        Assert.NotNull(refreshRequest);
        Assert.Contains("qn=80", refreshRequest.RequestAddress, StringComparison.Ordinal);
        Assert.Single(fixture.Backend.Requests);
    }

    [Fact]
    public async Task MediaStageReportsUnavailableWhenRefreshedDurlOmitsPendingOrder()
    {
        var playUrl = CreateDurlPlayUrl();
        playUrl.Durl =
        [
            new PlayUrlDurl
            {
                Order = 2,
                SourceAddress = "https://example.invalid/original-segment-2"
            }
        ];
        var apiClient = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (_, _) => Task.FromResult(
                """
                {
                  "code": 0,
                  "message": "success",
                  "data": {
                    "quality": 80,
                    "video_codecid": 7,
                    "durl": [
                      { "order": 1, "url": "https://example.invalid/refreshed-segment-1" }
                    ]
                  }
                }
                """)
        };
        using var fixture = await MediaStageFixture.CreateAsync(
            playUrl,
            downloadAudio: false,
            downloadVideo: true,
            apiClient: apiClient,
            backendResults:
            [
                DownloadTransferResult.Failed(
                    DownloadTransferFailureKind.ExpiredAddress,
                    "download.transfer.http-403")
            ]).ConfigureAwait(true);

        var result = await fixture.Stage.ExecuteAsync(
            fixture.Context,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.playback.selection-unavailable", result.Error?.Code);
        Assert.Single(fixture.Backend.Requests);
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

        var result = await fixture.Stage.ExecuteAsync(
            fixture.Context, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(completedMedia, fixture.Context.OutputMedia);
        Assert.Empty(fixture.Backend.Requests);
    }

    [Fact]
    public async Task MediaStageRejectsVideoAndAudioRequestWhenAudioCollectionIsEmpty()
    {
        var playUrl = CreateVideoOnlyPlayUrl();
        playUrl.Dash.Audio = [];
        using var fixture = await MediaStageFixture.CreateAsync(
            playUrl,
            downloadAudio: true,
            downloadVideo: true).ConfigureAwait(true);

        var result = await fixture.Stage.ExecuteAsync(
            fixture.Context,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.playback.selection-unavailable", result.Error?.Code);
        Assert.Null(fixture.Context.AudioFile);
        Assert.Null(fixture.Context.VideoFile);
        Assert.Empty(fixture.Backend.Requests);
    }

    [Fact]
    public async Task MediaStageReusesCompletedSelectedAudioWhenRefreshedPlaybackOmitsAudio()
    {
        using var fixture = await MediaStageFixture.CreateAsync(
            CreateVideoOnlyPlayUrl(),
            downloadAudio: true,
            downloadVideo: true).ConfigureAwait(true);
        var completedAudio = await fixture.AddCompletedAudioTransferAsync().ConfigureAwait(true);

        var result = await fixture.Stage.ExecuteAsync(
            fixture.Context,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(completedAudio.FilePath, fixture.Context.AudioFile);
        Assert.Equal(completedAudio.Key, fixture.Context.AudioTransferKey);
        Assert.NotNull(fixture.Context.VideoFile);
        Assert.Single(fixture.Backend.Requests);
    }

    [Fact]
    public async Task MediaStageRefreshesExpiredVideoWithoutRequestingCompletedAudioAgain()
    {
        var initialPlayback = CreateVideoOnlyPlayUrl();
        initialPlayback.Dash.Audio =
        [
            new PlayUrlDashVideo
            {
                Id = 30280,
                Codecs = "mp4a.40.2",
                BaseAddress = "https://example.invalid/original-audio"
            }
        ];
        var playbackRequests = new List<string>();
        var apiClient = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (request, _) =>
            {
                playbackRequests.Add(request.RequestAddress);
                return Task.FromResult(request.RequestAddress.StartsWith(
                    "https://www.bilibili.com/bangumi/play/",
                    StringComparison.Ordinal)
                    ? """
                      <script>const playurlSSRData = {"code":0,"result":{"video_info":{"quality":80,"durl":[],"dash":{"video":[{"id":80,"codecid":7,"codecs":"avc1","base_url":"https://example.invalid/refreshed-video"}],"audio":[]}}}};</script>
                      """
                    : """
                      {"code":0,"result":{"video_info":{"quality":80,"durl":[],"dash":{"video":[{"id":80,"codecid":7,"codecs":"avc1","base_url":"https://example.invalid/refreshed-video"}],"audio":[]}}}}
                      """);
            }
        };
        using var fixture = await MediaStageFixture.CreateAsync(
            initialPlayback,
            downloadAudio: true,
            downloadVideo: true,
            apiClient: apiClient,
            streamType: PlayStreamType.Bangumi,
            backendResults:
            [
                DownloadTransferResult.Failed(
                    DownloadTransferFailureKind.ExpiredAddress,
                    "download.transfer.http-403"),
                DownloadTransferResult.Succeeded()
            ]).ConfigureAwait(true);
        var completedAudio = await fixture.AddCompletedAudioTransferAsync().ConfigureAwait(true);

        var result = await fixture.Stage.ExecuteAsync(
            fixture.Context,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Single(playbackRequests);
        Assert.Equal(
            "https://www.bilibili.com/bangumi/play/ep3489",
            playbackRequests[0]);
        Assert.Equal(completedAudio.FilePath, fixture.Context.AudioFile);
        Assert.Equal(completedAudio.Key, fixture.Context.AudioTransferKey);
        Assert.Equal(2, fixture.Backend.Requests.Count);
        Assert.All(
            fixture.Backend.Requests,
            request => Assert.DoesNotContain(
                request.Urls,
                address => address.Contains("audio", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(
            "https://example.invalid/refreshed-video",
            Assert.Single(fixture.Backend.Requests[1].Urls));
    }

    [Fact]
    public async Task MediaStageReusesCompletedSelectedVideoWhenPlaybackOnlyProvidesPendingAudio()
    {
        using var fixture = await MediaStageFixture.CreateAsync(
            CreateAudioOnlyPlayUrl(),
            downloadAudio: true,
            downloadVideo: true).ConfigureAwait(true);
        var completedVideo = await fixture.AddCompletedVideoTransferAsync().ConfigureAwait(true);

        var result = await fixture.Stage.ExecuteAsync(
            fixture.Context,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(completedVideo.FilePath, fixture.Context.VideoFile);
        Assert.Equal(completedVideo.Key, fixture.Context.VideoTransferKey);
        var request = Assert.Single(fixture.Backend.Requests);
        Assert.Equal(
            "https://example.invalid/audio",
            Assert.Single(request.Urls));
    }

    [Fact]
    public async Task AudioOnlyMediaStageTransfersOnlySelectedAudio()
    {
        using var fixture = await MediaStageFixture.CreateAsync(
            CreateAudioOnlyPlayUrl(),
            downloadAudio: true,
            downloadVideo: false).ConfigureAwait(true);

        var result = await fixture.Stage.ExecuteAsync(
            fixture.Context,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(fixture.Context.AudioFile);
        Assert.Null(fixture.Context.VideoFile);
        var request = Assert.Single(fixture.Backend.Requests);
        Assert.Equal("https://example.invalid/audio", Assert.Single(request.Urls));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task MediaStageDoesNotSuppressMissingAudioForIncompleteOrUnusableTransfer(
        bool markCompleted,
        bool writeUsableFile)
    {
        using var fixture = await MediaStageFixture.CreateAsync(
            CreateVideoOnlyPlayUrl(),
            downloadAudio: true,
            downloadVideo: true).ConfigureAwait(true);
        await fixture.AddAudioTransferAsync(markCompleted, writeUsableFile).ConfigureAwait(true);

        var result = await fixture.Stage.ExecuteAsync(
            fixture.Context,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.playback.selection-unavailable", result.Error?.Code);
        Assert.Null(fixture.Context.AudioFile);
        Assert.Empty(fixture.Backend.Requests);
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
        Assert.Equal("download.playback.selection-unavailable", result.Error?.Code);
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
        Assert.Equal("download.playback.selection-unavailable", result.Error?.Code);
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

        var completion = FinalizeStage.CreateCompletionSummary(
            maximumBytesPerSecond: 1_250_000,
            timeProvider: new FixedTimeProvider(finishedAt));

        Assert.Equal(finishedAt.ToUnixTimeSeconds(), completion.FinishedTimestamp);
        Assert.False(string.IsNullOrEmpty(completion.FinishedTimeText));
        Assert.False(string.IsNullOrEmpty(completion.MaximumSpeedText));
    }

    private static DownloadExecutionContext CreateContext(
        ApplicationSettings settings,
        DownloadContentSelection? requestedContent = null,
        int resolutionId = 0,
        string videoCodecName = "",
        int audioCodecId = 0,
        PlayUrl? playUrl = null,
        PlayStreamType streamType = PlayStreamType.None)
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

    private static PlayUrl CreateAudioOnlyPlayUrl()
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

    private static PlayUrl CreateDurlPlayUrl(int quality = 80, int codecId = 7)
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

        public DownloadExecutionContext Context { get; private set; }

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
            Context = new DownloadExecutionContextFactory(_projections, _settings.Store)
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
                historyService,
                new SystemClock());
            var settings = new TestSettingsStore();
            if (apiClient != null)
            {
                settings.Store.Update(current => current with
                {
                    Video = current.Video with { VideoParseType = 0 }
                });
            }

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
            await projections.AddDownloadingAsync(
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
                    apiClient ?? new TestBilibiliApiClient()),
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

    private sealed class RecordingMediaBackend(
        params DownloadTransferResult[] results) : ITransferBackend
    {
        private readonly Queue<DownloadTransferResult> _results = new(results);

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
