using DownKyi.Application.Bilibili;
using DownKyi.Application.Downloads;
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
using static DownKyi.Tests.DownloadPipelineTestSupport;

namespace DownKyi.Tests;

public sealed class DownloadMediaStageTests
{
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
    public async Task PausedTransferStopsMediaStageWithoutPublishingMedia()
    {
        using var fixture = await MediaStageFixture.CreateAsync(
            CreateVideoOnlyPlayUrl(),
            downloadAudio: false,
            downloadVideo: true,
            backendResults: [DownloadTransferResult.Paused()]).ConfigureAwait(true);

        await Assert.ThrowsAsync<DownloadPausedException>(() =>
            fixture.Stage.ExecuteAsync(
                fixture.Context,
                TestContext.Current.CancellationToken));

        Assert.Single(fixture.Backend.Requests);
        Assert.Null(fixture.Context.VideoFile);
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
}
