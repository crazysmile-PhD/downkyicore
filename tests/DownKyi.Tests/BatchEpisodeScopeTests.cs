using DownKyi.Application.Media;
using DownKyi.Core.BiliApi;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Domain.Downloads;
using DownKyi.Services.Download;
using DownKyi.Services.Media;
using DownKyi.Services.Video;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class BatchEpisodeScopeTests
{
    private static readonly long[] MainEpisodes = [1001, 1002];
    private static readonly long[] CourseEpisodes = [2001, 2002];
    private static readonly long[] SelectedEpisodes = [1002, 1003];

    [Theory]
    [InlineData(1001L)]
    [InlineData(1002L)]
    [InlineData(1003L)]
    public async Task BangumiEpisodeSelectsOnlyItsOriginalMainOrExtraEntry(long episodeId)
    {
        using var fixture = new Fixture();
        var result = await fixture.PrepareAsync(new BilibiliBangumiEpisodeInput(episodeId)).ConfigureAwait(true);
        var page = Assert.Single(Assert.Single(result!.Sections).Pages).Page;
        Assert.Equal(episodeId, page.SourceEpisodeId);
        Assert.Equal(episodeId, page.EpisodeId);
        Assert.Single(fixture.Requests);
    }

    [Fact]
    public async Task TwoEpisodesFromOneSeasonDoNotExpandIntoRepeatedSeasonCandidates()
    {
        using var fixture = new Fixture();
        var candidates = new List<long>();
        foreach (var episode in SelectedEpisodes)
        {
            var result = await fixture.PrepareAsync(new BilibiliBangumiEpisodeInput(episode)).ConfigureAwait(true);
            candidates.AddRange(result!.Sections.SelectMany(section => section.Pages).Select(page => page.Page.SourceEpisodeId));
        }
        Assert.Equal(SelectedEpisodes, candidates);
        Assert.Equal(2, fixture.Requests.Count);
    }

    [Theory]
    [InlineData("https://www.bilibili.com/bangumi/play/ss99")]
    [InlineData("https://www.bilibili.com/bangumi/media/md88")]
    public async Task SeasonAndMediaInputsKeepTheWholeMainSeason(string address)
    {
        using var fixture = new Fixture();
        var result = await fixture.PrepareAsync((BilibiliMediaInput)BilibiliInputParser.Parse(address)!).ConfigureAwait(true);
        Assert.Equal(MainEpisodes, result!.Sections.SelectMany(section => section.Pages).Select(page => page.Page.SourceEpisodeId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CourseEpisodeAndSeasonUseTheirOwnScope(bool singleEpisode)
    {
        using var fixture = new Fixture();
        BilibiliMediaInput input = singleEpisode ? new BilibiliCheeseEpisodeInput(2002) : new BilibiliCheeseSeasonInput(199);
        var result = await fixture.PrepareAsync(input).ConfigureAwait(true);
        var ids = result!.Sections.SelectMany(section => section.Pages).Select(page => page.Page.SourceEpisodeId);
        if (singleEpisode) Assert.Equal(2002, Assert.Single(ids));
        else Assert.Equal(CourseEpisodes, ids);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingEpisodeFailsBeforePlaybackAndNeverFallsBackToASeason(bool course)
    {
        using var fixture = new Fixture();
        BilibiliMediaInput input = course ? new BilibiliCheeseEpisodeInput(9999) : new BilibiliBangumiEpisodeInput(9999);
        await Assert.ThrowsAsync<BilibiliApiResponseException>(() => fixture.PrepareAsync(input, media: true)).ConfigureAwait(true);
        Assert.Single(fixture.Requests);
    }

    [Fact]
    public async Task ExtraRedirectKeepsSelectionIdentityWhileResolvingTheCorrectOrdinaryVideoPage()
    {
        using var fixture = new Fixture();
        var result = await fixture.PrepareAsync(new BilibiliBangumiEpisodeInput(1004)).ConfigureAwait(true);
        var page = Assert.Single(Assert.Single(result!.Sections).Pages).Page;
        Assert.Equal(1004, page.SourceEpisodeId);
        Assert.Equal(-1, page.EpisodeId);
        Assert.Equal(9002, page.Cid);
        Assert.Equal(2, page.Page);
        Assert.Equal(2, fixture.Requests.Count);
    }

    [Fact]
    public async Task PlaybackDiscoveryRunsOnlyForTheSelectedEpisode()
    {
        using var fixture = new Fixture();
        var result = await fixture.PrepareAsync(new BilibiliBangumiEpisodeInput(1002), media: true).ConfigureAwait(true);
        Assert.Single(Assert.Single(result!.Sections).Pages);
        var playbackRequests = fixture.Requests.Where(uri => uri.AbsolutePath.Contains("play", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(playbackRequests);
        Assert.All(playbackRequests, uri => Assert.Contains("1002", uri.PathAndQuery, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DetailDownloadAllStillIncludesMainAndExtraSectionsAfterOpeningAnEpisode()
    {
        using var fixture = new Fixture();
        var source = await fixture.Factory.CreateAsync(new ContentDownloadItem(new BilibiliBangumiEpisodeInput(1002), true),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        var result = await fixture.Preparation.PrepareAsync(source.GetVideoView(TestContext.Current.CancellationToken)!,
            source.GetVideoSections(false, TestContext.Current.CancellationToken)!,
            DownloadContentSelection.None with { Cover = true }, isAll: true, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(4, result.Sections.Sum(section => section.Pages.Count));
        Assert.Equal(2, result.Sections.Count);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TestSettingsStore _settings = new();
        public List<Uri> Requests { get; } = [];
        public ContentInfoServiceFactory Factory { get; }
        public DownloadPreparationService Preparation { get; }

        public Fixture()
        {
            var client = new TestBilibiliApiClient
            {
                GetStringAsyncHandler = (request, _) =>
                {
                    var uri = new Uri(request.RequestAddress);
                    Requests.Add(uri);
                    var response = uri.AbsolutePath switch
                    {
                        "/pgc/view/web/season" => SeasonResponse,
                        "/pgc/review/user" => """{"code":0,"result":{"media":{"season_id":99}}}""",
                        "/pugv/view/web/season" => CourseResponse,
                        "/x/web-interface/wbi/view" => OrdinaryVideoResponse,
                        "/pgc/player/web/v2/playurl" or "/pgc/player/web/playurl" => PlaybackResponse,
                        _ when uri.AbsolutePath.StartsWith("/bangumi/play/ep", StringComparison.Ordinal) => "<html></html>",
                        _ => throw new InvalidOperationException("Unexpected fixture request.")
                    };
                    return Task.FromResult(response);
                }
            };
            var keys = new TestWbiKeyProvider();
            var tags = new VideoTagProvider(client);
            Factory = new ContentInfoServiceFactory(_settings.Store, tags, keys, client);
            Preparation = new DownloadPreparationService(PlayStreamType.Bangumi, _settings.Store, tags, keys, client, NullLogger.Instance);
        }

        public async Task<PreparedDownload?> PrepareAsync(BilibiliMediaInput input, bool media = false)
        {
            var source = await Factory.CreateAsync(new ContentDownloadItem(input, true),
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            return await Preparation.PrepareAsync(input, source,
                DownloadContentSelection.None with { Video = media, Cover = !media },
                TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
        public void Dispose() => _settings.Dispose();
    }

    private const string SeasonResponse =
        """
        {"code":0,"result":{"season_id":99,"type":4,"title":"Season","positive":{"id":1,"title":"Main"},
        "episodes":[
          {"id":1001,"aid":3001,"cid":4001,"bvid":"BV17x411w7KC","share_copy":"First","duration":60000},
          {"id":1002,"aid":3002,"cid":4002,"bvid":"BV17x411w7KC","share_copy":"Second","duration":60000}],
        "section":[{"id":2,"title":"Extras","episodes":[
          {"id":1003,"aid":3003,"cid":4003,"bvid":"BV17x411w7KC","title":"Extra"},
          {"id":1004,"aid":0,"cid":0,"title":"Redirect","link":"https://www.bilibili.com/video/BV17x411w7KC?p=2"}]}]}}
        """;
    private const string CourseResponse =
        """
        {"code":0,"data":{"season_id":199,"title":"Course","episodes":[
          {"id":2001,"aid":5001,"cid":6001,"title":"First","duration":60},
          {"id":2002,"aid":5002,"cid":6002,"title":"Second","duration":60}]}}
        """;
    private const string OrdinaryVideoResponse =
        """
        {"code":0,"data":{"aid":170001,"bvid":"BV17x411w7KC","title":"Ordinary","pages":[
          {"page":1,"cid":9001,"part":"One","duration":60},
          {"page":2,"cid":9002,"part":"Two","duration":60}]}}
        """;
    private const string PlaybackResponse =
        """
        {"code":0,"result":{"video_info":{"quality":80,"video_codecid":7,"durl":[{"order":1,"url":"https://media.invalid/video.flv"}]}}}
        """;
}
