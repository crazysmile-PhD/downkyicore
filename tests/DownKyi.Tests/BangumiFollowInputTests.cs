using DownKyi.Application.Desktop;
using DownKyi.Application.Media;
using DownKyi.Commands;
using DownKyi.Core.BiliApi;
using DownKyi.Core.BiliApi.Users.Models;
using DownKyi.Presentation;
using DownKyi.Services.Media;
using DownKyi.Services.UserSpace;
using DownKyi.Services.Video;
using DownKyi.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class BangumiFollowInputTests
{
    private static readonly string[] SeasonRequests = ["/pgc/view/web/season"];
    private static readonly string[] MediaRequests = ["/pgc/review/user", "/pgc/view/web/season"];

    private const string FollowResponse = """
        {"code":0,"data":{"total":6,"list":[
          {"media_id":1001,"season_id":2001,"title":"known season"},
          {"media_id":1002,"title":"missing season"},
          {"media_id":1003,"season_id":-1,"title":"invalid season"},
          {"media_id":0,"season_id":2004,"title":"season only"},
          {"media_id":0,"season_id":0,"title":"missing identity"},
          {"media_id":-1,"season_id":-1,"title":"invalid identity"}
        ]}}
        """;
    private const string SeasonResponse = """
        {"code":0,"result":{"episodes":[
          {"id":9001,"cid":7001,"aid":1,"bvid":"BV17x411w7KC","share_copy":"part 1","duration":1000},
          {"id":9002,"cid":7002,"aid":2,"bvid":"BV17x411w7KC","share_copy":"part 2","duration":1000}
        ]}}
        """;

    [Theory]
    [InlineData(0, BangumiType.ANIME, true)]
    [InlineData(0, BangumiType.ANIME, false)]
    [InlineData(1, BangumiType.EPISODE, true)]
    [InlineData(1, BangumiType.EPISODE, false)]
    public async Task FollowNavigationAndDownloadsShareIdentityAndPreserveSeasonPages(
        int tabIndex, BangumiType expectedType, bool onlySelected)
    {
        using var settings = new TestSettingsStore();
        var requests = new List<Uri>();
        var navigation = new TestNavigationService();
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (request, _) =>
            {
                var uri = new Uri(request.RequestAddress);
                requests.Add(uri);
                return Task.FromResult(uri.AbsolutePath switch
                {
                    "/x/space/bangumi/follow/list" => FollowResponse,
                    "/pgc/review/user" => ResolveMediaResponse(uri),
                    "/pgc/view/web/season" => SeasonResponse,
                    _ => throw new InvalidOperationException("Unexpected endpoint.")
                });
            }
        };
        var coordinator = new UserSpacePageCoordinator(settings.Store, new TestWbiKeyProvider(), navigation,
            NullLogger<UserSpacePageCoordinator>.Instance, client);
        var downloads = new RecordingDownloads();
        using var viewModel = new MyBangumiFollowViewModel(new TestDesktopInteractionContext(navigation),
            downloads, coordinator, NullLogger<MyBangumiFollowViewModel>.Instance);
        viewModel.OnNavigatedTo(new AppNavigationContext(AppNavigationRegion.Main, AppRoute.MyBangumiFollow,
            AppRoute.MySpace, 42L, new AppNavigationParameters(new Dictionary<string, object?> { ["Parameter"] = 42L })));
        if (tabIndex != 0)
        {
            requests.Clear();
            viewModel.SelectTabId = tabIndex;
            viewModel.TabHeadersCommand.Execute(viewModel.TabHeaders[tabIndex]);
        }

        Assert.Equal(expectedType.ToString("D"), Query(Assert.Single(requests), "type"));
        Assert.True(viewModel.IsEnabled);
        Assert.Equal(6, viewModel.Medias.Count);
        for (var index = 0; index < viewModel.Medias.Count; index++)
        {
            viewModel.Medias[index].IsSelected = index != 1;
            viewModel.Medias[index].TitleCommand.Execute(null);
        }

        BilibiliMediaInput[] expectedInputs =
        [
            new BilibiliBangumiSeasonInput(2001),
            new BilibiliBangumiMediaInput(1002),
            new BilibiliBangumiMediaInput(1003),
            new BilibiliBangumiSeasonInput(2004)
        ];
        Assert.Equal(expectedInputs, navigation.Requests.Select(request =>
        {
            Assert.Equal(AppRoute.VideoDetail, request.Route);
            Assert.Equal(AppRoute.MyBangumiFollow, request.Parent);
            return Assert.IsType<VideoNavigationPayload>(request.Parameter).Input;
        }));

        await ExecuteAsync(onlySelected ? viewModel.AddToDownloadCommand : viewModel.AddAllToDownloadCommand,
            viewModel.DownloadCommandGate);

        Assert.Equal(onlySelected, downloads.OnlySelected);
        Assert.Equal(expectedInputs, downloads.Items.Select(item => item.Input));
        Assert.Equal([true, false, true, true], downloads.Items.Select(item => item.IsSelected));
        Assert.Equal(["known season", "missing season", "invalid season", "season only"],
            downloads.Items.Select(item => item.DisplayName));

        var factory = new ContentInfoServiceFactory(settings.Store, new NoTags(), new TestWbiKeyProvider(), client);
        long[] expectedSeasons = [2001, 2002, 2003, 2004];
        for (var index = 0; index < downloads.Items.Count; index++)
        {
            requests.Clear();
            var item = downloads.Items[index];
            var info = await factory.CreateAsync(item, TestContext.Current.CancellationToken);
            var pages = info.GetVideoPages(TestContext.Current.CancellationToken);

            Assert.NotNull(pages);
            Assert.Equal([7001L, 7002L], pages.Select(page => page.Cid));
            Assert.Equal([9001L, 9002L], pages.Select(page => page.EpisodeId));
            Assert.Equal(item.Input is BilibiliBangumiSeasonInput ? SeasonRequests : MediaRequests,
                requests.Select(request => request.AbsolutePath));
            Assert.Equal(expectedSeasons[index].ToString(System.Globalization.CultureInfo.InvariantCulture),
                Query(requests[^1], "season_id"));
        }
    }

    [Theory]
    [InlineData(-404)]
    [InlineData(-101)]
    public async Task KnownSeasonApiFailureDoesNotGuessAnotherSeasonThroughMedia(int code)
    {
        using var settings = new TestSettingsStore();
        var requests = new List<Uri>();
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (request, _) =>
            {
                requests.Add(new Uri(request.RequestAddress));
                return Task.FromResult($$"""{"code":{{code}},"message":"unavailable"}""");
            }
        };
        var navigation = new TestNavigationService();
        var media = new BangumiFollowMedia(navigation, AppRoute.MyBangumiFollow) { SeasonId = 2001, MediaId = 1001 };
        media.TitleCommand.Execute(null);
        var input = Assert.IsType<VideoNavigationPayload>(Assert.Single(navigation.Requests).Parameter).Input;
        var factory = new ContentInfoServiceFactory(settings.Store, new NoTags(), new TestWbiKeyProvider(), client);

        var exception = await Assert.ThrowsAsync<BilibiliApiResponseException>(() =>
            factory.CreateAsync(new ContentDownloadItem(input, true), TestContext.Current.CancellationToken));

        Assert.Equal(code, exception.Code);
        Assert.Equal("/pgc/view/web/season", Assert.Single(requests).AbsolutePath);
        Assert.Equal("2001", Query(requests[0], "season_id"));
    }

    private static string ResolveMediaResponse(Uri uri)
    {
        var seasonId = long.Parse(Query(uri, "media_id"), System.Globalization.CultureInfo.InvariantCulture) + 1000;
        return $$"""{"code":0,"result":{"media":{"season_id":{{seasonId}} } } }""";
    }

    private static string Query(Uri uri, string key)
    {
        return System.Web.HttpUtility.ParseQueryString(uri.Query)[key]
               ?? throw new InvalidOperationException("Required fixture query is missing.");
    }

    private static async Task ExecuteAsync(DownKyiAsyncDelegateCommand command, DownKyiAsyncCommandGate gate)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.IsExecutingChanged += OnChanged;
        try
        {
            command.Execute(null);
            await completed.Task.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
        finally
        {
            gate.IsExecutingChanged -= OnChanged;
        }

        void OnChanged(object? sender, EventArgs args)
        {
            if (!gate.IsExecuting)
            {
                completed.TrySetResult();
            }
        }
    }

    private sealed class RecordingDownloads : IContentDownloadCoordinator
    {
        public IReadOnlyList<ContentDownloadItem> Items { get; private set; } = [];
        public bool OnlySelected { get; private set; }

        public Task<ContentDownloadBatchResult?> AddAsync(IReadOnlyList<ContentDownloadItem> items, bool onlySelected,
            CancellationToken cancellationToken, IProgress<ContentDownloadBatchProgress>? progress = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Items = items;
            OnlySelected = onlySelected;
            return Task.FromResult<ContentDownloadBatchResult?>(null);
        }
    }

    private sealed class NoTags : IVideoTagProvider
    {
        public Task<IReadOnlyList<string>> GetTagsAsync(string bvid, long cid, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Bangumi metadata must not fetch ordinary video tags.");
        }
    }
}
