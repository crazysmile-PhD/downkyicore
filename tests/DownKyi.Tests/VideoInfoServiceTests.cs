using DownKyi.Core.BiliApi.Video.Models;
using DownKyi.Services;
using DownKyi.Services.Video;
using CoreVideoPage = DownKyi.Core.BiliApi.Video.Models.VideoPage;

namespace DownKyi.Tests;

public sealed class VideoInfoServiceTests
{
    [Fact]
    public void VideoPagesUseDurationFromDetailMetadata()
    {
        using var settings = new TestSettingsStore();
        var client = new TestBilibiliApiClient();
        var service = new VideoInfoService(
            new VideoView
            {
                Aid = 42,
                Bvid = "BV1duration",
                Title = "Duration fixture",
                Pages =
                [
                    new CoreVideoPage
                    {
                        Cid = 84,
                        Page = 1,
                        Part = "Page",
                        Duration = 3723
                    }
                ]
            },
            settings.Store,
            new VideoTagProvider(client),
            new TestWbiKeyProvider(),
            client);

        var page = Assert.Single(service.GetVideoPages(TestContext.Current.CancellationToken)!);

        Assert.Equal("1h2m3s", page.Duration);
    }

    [Fact]
    public void VideoPagesKeepUnavailableDurationWhenDetailMetadataHasNoDuration()
    {
        using var settings = new TestSettingsStore();
        var client = new TestBilibiliApiClient();
        var service = new VideoInfoService(
            new VideoView
            {
                Aid = 42,
                Bvid = "BV1duration",
                Title = "Duration fixture",
                Pages =
                [
                    new CoreVideoPage
                    {
                        Cid = 84,
                        Page = 1,
                        Part = "Page",
                        Duration = 0
                    }
                ]
            },
            settings.Store,
            new VideoTagProvider(client),
            new TestWbiKeyProvider(),
            client);

        var page = Assert.Single(service.GetVideoPages(TestContext.Current.CancellationToken)!);

        Assert.Equal("N/A", page.Duration);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UgcPagesUseDurationAndPublicationTimeFromDetailMetadata(bool multipart)
    {
        using var settings = new TestSettingsStore();
        var client = new TestBilibiliApiClient();
        var service = new VideoInfoService(
            new VideoView
            {
                Aid = 42,
                Bvid = "BV1duration",
                Title = "Duration fixture",
                UgcSeason = new UgcSeason
                {
                    Sections =
                    [
                        new UgcSection
                        {
                            Id = 7,
                            Title = "Section",
                            Episodes =
                            [
                                new UgcEpisode
                                {
                                    Aid = 43,
                                    Bvid = "BV1episode",
                                    Cid = 85,
                                    Title = "Episode",
                                    Arc = new UgcArc { Pubdate = 1_700_000_000, Ctime = 1_600_000_000 },
                                    Page = new CoreVideoPage
                                    {
                                        Cid = 85,
                                        Page = 1,
                                        Duration = 61
                                    },
                                    Pages = multipart
                                        ? [
                                            new CoreVideoPage { Cid = 85, Page = 1, Duration = 61 },
                                            new CoreVideoPage { Cid = 86, Page = 2, Duration = 61 }
                                        ]
                                        : []
                                }
                            ]
                        }
                    ]
                }
            },
            settings.Store,
            new VideoTagProvider(client),
            new TestWbiKeyProvider(),
            client);

        var section = Assert.Single(service.GetVideoSections(
            cancellationToken: TestContext.Current.CancellationToken)!);
        Assert.Equal(multipart ? 2 : 1, section.VideoPages.Count);
        Assert.All(section.VideoPages, page =>
        {
            Assert.Equal("1m1s", page.Duration);
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000).LocalDateTime, page.OriginalPublishTime);
        });
    }
}
