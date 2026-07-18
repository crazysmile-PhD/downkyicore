using DownKyi.Core.Aria2cNet.Client.Entity;
using DownKyi.Core.BiliApi.Bangumi.Models;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.Favorites;
using DownKyi.Core.BiliApi.Favorites.Models;
using DownKyi.Core.BiliApi.Login.Models;
using DownKyi.Core.BiliApi.Users.Models;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.BiliApi.VideoStream.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.Tests;

public sealed class BiliApiModelContractTests
{
    [Fact]
    public void FavoritesBvidFieldsRemainDistinct()
    {
        const string json = """{"bv_id":"legacy","bvid":"current"}""";

        var media = JsonConvert.DeserializeObject<FavoritesMedia>(json);
        var mediaId = JsonConvert.DeserializeObject<FavoritesMediaId>(json);

        Assert.Equal("legacy", media?.LegacyBvid);
        Assert.Equal("current", media?.Bvid);
        Assert.Equal("legacy", mediaId?.LegacyBvid);
        Assert.Equal("current", mediaId?.Bvid);
    }

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
        Assert.Equal("https://example.invalid/segment", durl.SourceAddress);
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
        var video = Assert.Single(playUrl.Dash.Video);
        Assert.Equal(80, video.Id);
        Assert.Equal("https://example.invalid/video", video.BaseAddress);
        Assert.Equal(30280, Assert.Single(playUrl.Dash.Audio).Id);
        Assert.Equal(80, Assert.Single(playUrl.SupportFormats).Quality);
    }

    [Fact]
    public void LoginAndAriaAddressesPreserveWireNames()
    {
        var loginUrl = JsonConvert.DeserializeObject<LoginUrl>("""
            { "qrcode_key": "key", "url": "https://example.invalid/qr" }
            """);
        var loginStatus = JsonConvert.DeserializeObject<LoginStatusData>("""
            { "url": "https://example.invalid/callback", "refresh_token": "token" }
            """);
        var ariaUri = JsonConvert.DeserializeObject<AriaUri>("""
            { "status": "used", "uri": "https://example.invalid/file" }
            """);
        var ariaServer = JsonConvert.DeserializeObject<AriaResultServer>("""
            { "currentUri": "https://example.invalid/current", "uri": "https://example.invalid/original" }
            """);

        Assert.Equal("https://example.invalid/qr", loginUrl?.QrCodeAddress);
        Assert.Equal("https://example.invalid/callback", loginStatus?.RedirectAddress);
        Assert.Equal("https://example.invalid/file", ariaUri?.Address);
        Assert.Equal("https://example.invalid/current", ariaServer?.CurrentAddress);
        Assert.Equal("https://example.invalid/original", ariaServer?.Address);
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
    [Theory]
    [InlineData("https://www.bilibili.com/list/3546801722362343")]
    [InlineData("https://www.bilibili.com/list/3546801722362343?oid=114070741065497")]
    [InlineData("https://m.bilibili.com/list/3546801722362343/")]
    public void UserVideoListUrlsExposeTheUploaderMid(string input)
    {
        Assert.True(ParseEntrance.IsUserVideoListUrl(input));
        Assert.Equal(3546801722362343, ParseEntrance.GetUserVideoListId(input));
    }

    [Theory]
    [InlineData("https://www.bilibili.com/list/ml1329019876")]
    [InlineData("https://www.bilibili.com/list/not-a-mid")]
    public void UserVideoListUrlsRejectOtherListTypes(string input)
    {
        Assert.False(ParseEntrance.IsUserVideoListUrl(input));
        Assert.Equal(-1, ParseEntrance.GetUserVideoListId(input));
    }

    [Fact]
    public void UserVideoListResponsePreservesPagingAndArchiveMetadata()
    {
        var response = JsonConvert.DeserializeObject<UserVideoListOrigin>("""
            {
              "data": {
                "archives": [{
                  "aid": 114070741065497,
                  "bvid": "BV1T7P7eFEn5",
                  "pic": "https://example.invalid/cover.jpg",
                  "title": "Sample video",
                  "duration": 294,
                  "pubdate": 1740582876,
                  "attr": 0,
                  "author": { "mid": 3546801722362343, "name": "Uploader" },
                  "stat": { "view": 405 }
                }],
                "page": { "count": 1224, "pn": 1, "ps": 20 }
              }
            }
            """);

        Assert.NotNull(response?.Data);
        Assert.Equal(1224, response.Data.Page.Count);
        var archive = Assert.Single(response.Data.Archives);
        Assert.Equal("BV1T7P7eFEn5", archive.Bvid);
        Assert.Equal(3546801722362343, archive.Author.Mid);
        Assert.Equal(405, archive.Stat.View);
    }

    [Fact]
    public void FavoritesSearchUrlEncodesTheCurrentFolderKeyword()
    {
        var url = FavoritesResource.BuildFavoritesMediaUrl(123, 2, 20, "  猫 & 狗  ");

        Assert.Contains("media_id=123", url, StringComparison.Ordinal);
        Assert.Contains("pn=2", url, StringComparison.Ordinal);
        Assert.EndsWith(
            $"&keyword={Uri.EscapeDataString("猫 & 狗")}",
            url,
            StringComparison.Ordinal);
    }
}
