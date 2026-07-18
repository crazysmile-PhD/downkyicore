using DownKyi.Core.BiliApi;
using DownKyi.Core.BiliApi.Video;
using DownKyi.Core.BiliApi.Video.Models;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.BiliApi.VideoStream.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.Tests;

public sealed class BvFixtureContractTests
{
    private static readonly string SampleDirectory = Path.Combine(
        FindRepositoryRoot(),
        "tests",
        "DownKyi.Core.Tests",
        "BiliApi",
        "JsonSamples");

    [Fact]
    public void Bv1U7V66FEiKFixturesPreserveInfoPageAndPlaybackContracts()
    {
        var view = Read<VideoViewOrigin>("video-view-BV1U7V66FEiK.json");
        var pages = Read<VideoPagelist>("video-pagelist-BV1U7V66FEiK.json");
        var playUrl = Read<PlayUrlOrigin>("playurl-BV1U7V66FEiK.json");

        Assert.Equal("BV1U7V66FEiK", view.Data?.Bvid);
        Assert.Equal(1919810, Assert.Single(view.Data?.Pages ?? []).Cid);
        Assert.Equal(1919810, Assert.Single(pages.Data ?? []).Cid);
        var payload = VideoStreamApi.SelectPlayUrlPayload(
            playUrl,
            VideoStreamApi.PlayUrlPayloadField.Data,
            "BV1U7V66FEiK");
        Assert.Single(payload.Dash.Video);
        Assert.Single(payload.Dash.Audio);
    }

    [Fact]
    public void VideoInfoRejectsMissingPagesAndCid()
    {
        var missingPages = new VideoView { Aid = 1, Bvid = "BV1U7V66FEiK" };
        var missingCid = new VideoView
        {
            Aid = 1,
            Bvid = "BV1U7V66FEiK",
            Pages = [new VideoPage()]
        };

        Assert.Throws<BilibiliApiResponseException>(() => VideoInfo.ValidateVideoView(missingPages));
        Assert.Throws<BilibiliApiResponseException>(() => VideoInfo.ValidateVideoView(missingCid));
    }

    private static T Read<T>(string name)
    {
        return JsonConvert.DeserializeObject<T>(
                   File.ReadAllText(Path.Combine(SampleDirectory, name)))
               ?? throw new InvalidDataException($"Sample '{name}' did not deserialize.");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "DownKyi.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
