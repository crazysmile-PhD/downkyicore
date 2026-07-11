using DownKyi.Core.BiliApi.Bangumi.Models;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.Users.Models;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.BiliApi.VideoStream.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.Tests;

public sealed class BiliApiModelContractTests
{
    private static readonly string[] ExpectedStyles = { "sci-fi", "adventure" };

    [Fact]
    public void BangumiStylesDeserializesJsonArray()
    {
        var season = JsonConvert.DeserializeObject<BangumiSeason>("""
            { "styles": ["sci-fi", "adventure"] }
            """);

        Assert.NotNull(season);
        Assert.Equal(ExpectedStyles, season.Styles);
    }

    [Fact]
    public void VideoPlayPageUrlPrefersBvid()
    {
        var url = VideoStreamApi.BuildVideoPlayPageUrl(170001, "BV17x411w7KC", 2);

        Assert.Equal("https://www.bilibili.com/video/BV17x411w7KC/?p=2", url);
    }

    [Fact]
    public void VideoPlayPageUrlFallsBackToAvid()
    {
        var url = VideoStreamApi.BuildVideoPlayPageUrl(170001, string.Empty, 3);

        Assert.Equal("https://www.bilibili.com/video/av170001/?p=3", url);
    }

    [Fact]
    public void ParseEntranceRejectsNullInput()
    {
        Assert.Throws<ArgumentNullException>(() => ParseEntrance.IsAvId(null!));
    }

    [Fact]
    public void DurlBackupUrlsDeserializeAsReadOnlyContract()
    {
        var durl = JsonConvert.DeserializeObject<PlayUrlDurl>("""
            { "order": 1, "url": "https://example.invalid/segment", "backup_url": ["https://backup.invalid/segment"] }
            """);

        Assert.NotNull(durl);
        Assert.Equal("https://backup.invalid/segment", Assert.Single(durl.BackupUrl));
    }

    [Fact]
    public void PlayUrlCollectionsDeserializeAsReadOnlyContracts()
    {
        var playUrl = JsonConvert.DeserializeObject<PlayUrl>("""
            {
              "accept_description": ["1080P"],
              "accept_quality": [80],
              "durl": [{ "order": 2, "url": "https://example.invalid/2", "backup_url": [] }],
              "dash": {
                "video": [{ "id": 80, "base_url": "https://example.invalid/video", "backup_url": [] }],
                "audio": [{ "id": 30280, "base_url": "https://example.invalid/audio", "backup_url": [] }]
              },
              "support_formats": [{ "quality": 80, "new_description": "1080P" }]
            }
            """);

        Assert.NotNull(playUrl);
        Assert.Equal(2, Assert.Single(playUrl.Durl).Order);
        Assert.Equal(80, Assert.Single(playUrl.Dash.Video).Id);
        Assert.Equal(30280, Assert.Single(playUrl.Dash.Audio).Id);
        Assert.Equal(80, Assert.Single(playUrl.SupportFormats).Quality);
    }

    [Fact]
    public void SpaceSeriesPagePropertiesPreserveWireNames()
    {
        var page = JsonConvert.DeserializeObject<SpaceSeasonsSeriesPage>("""
            { "page_num": 2, "page_size": 20, "total": 42 }
            """);

        Assert.NotNull(page);
        Assert.Equal(2, page.PageNum);
        Assert.Equal(20, page.PageSize);
        Assert.Equal(42, page.Total);
    }
}
