using DownKyi.Application.Desktop;
using DownKyi.Application.Media;
using DownKyi.Application.Settings;
using DownKyi.Services;
using DownKyi.Services.Account;
using DownKyi.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class SearchInputTests
{
    private const string VideoAddress = "https://www.bilibili.com/video/BV17x411w7KC";

    [Theory]
    [InlineData("BV17x411w7KC")]
    [InlineData("av170001")]
    [InlineData("ep1002")]
    [InlineData("ss2001")]
    [InlineData("md1001")]
    [InlineData("https://www.bilibili.com/cheese/play/ep3489")]
    [InlineData("https://www.bilibili.com/cheese/play/ss205")]
    public void HomeStartupAndClipboardSearchKeepTheSameMediaIdentity(string identity)
    {
        using var settings = new TestSettingsStore();
        var expected = Assert.IsAssignableFrom<BilibiliMediaInput>(BilibiliInputParser.Parse(identity));
        var navigation = new TestNavigationService();
        var search = new SearchService(settings.Store, navigation);
        using var index = CreateIndex(settings.Store, navigation, search);
        foreach (var text in new[] { identity, "【fixture】" + expected.CanonicalUri.AbsoluteUri,
                     "分享 " + expected.CanonicalUri.AbsoluteUri + " trailing words" })
        {
            Assert.True(search.BiliInput(text, AppRoute.Index, suppressDuplicateVideoNavigation: true));
            AssertVideoRequest(expected, navigation, suppressDuplicate: true);
            navigation.Requests.Clear();

            index.InputText = text;
            index.InputCommand.Execute(null);
            AssertVideoRequest(expected, navigation, suppressDuplicate: false);
            Assert.Equal(string.Empty, index.InputText);
            navigation.Requests.Clear();

            var startup = new StartupIndexViewModel(settings.Store.Current.User, _ => { }, queued =>
            {
                Assert.Equal(text, queued);
                Assert.True(search.BiliInput(queued, AppRoute.Index));
            })
            { InputText = text };
            startup.InputCommand.Execute(null);
            AssertVideoRequest(expected, navigation, suppressDuplicate: false);
            navigation.Requests.Clear();
        }
    }

    [Theory]
    [InlineData("https://space.bilibili.com/42", AppRoute.MySpace, 42L)]
    [InlineData("https://space.bilibili.com/43", AppRoute.UserSpace, 43L)]
    [InlineData("https://www.bilibili.com/list/ml100", AppRoute.PublicFavorites, 100L)]
    [InlineData("https://www.bilibili.com/list/42", AppRoute.Publication, 42L)]
    [InlineData("https://www.bilibili.com/list/42?sid=99", AppRoute.SeasonsSeries, 42L)]
    public void SharedNonMediaLinksKeepExistingTypedRoutes(string address, AppRoute route, long id)
    {
        using var settings = new TestSettingsStore();
        settings.Store.Update(current => current with { User = current.User with { Mid = 42, IsLogin = true } });
        var navigation = new TestNavigationService();
        var search = new SearchService(settings.Store, navigation);

        Assert.True(search.BiliInput("【fixture】" + address, AppRoute.Index));

        var request = Assert.Single(navigation.Requests);
        Assert.Equal(route, request.Route);
        Assert.Equal(AppRoute.Index, request.Parent);
        switch (route)
        {
            case AppRoute.Publication:
                Assert.Equal(id, Assert.IsType<PublicationNavigationPayload>(request.Parameter).Mid);
                break;
            case AppRoute.SeasonsSeries:
                var series = Assert.IsType<SeriesNavigationPayload>(request.Parameter);
                Assert.Equal(id, series.Mid);
                Assert.Equal(99, series.SeriesId);
                break;
            default:
                Assert.Equal(id, Assert.IsType<long>(request.Parameter));
                break;
        }
    }

    [Theory]
    [InlineData("not a supported input")]
    [InlineData(" \t ")]
    [InlineData("fixture https://www.bilibili.com.evil.invalid/video/BV17x411w7KC")]
    [InlineData("fixture https://user@www.bilibili.com/video/BV17x411w7KC")]
    [InlineData("fixture https://www.bilibili.com:444/video/BV17x411w7KC")]
    [InlineData("fixture ftp://www.bilibili.com/video/BV17x411w7KC")]
    [InlineData("fixture https://evil.invalid/?next=https://www.bilibili.com/video/BV17x411w7KC")]
    [InlineData("prefixhttps://www.bilibili.com/video/BV17x411w7KC")]
    [InlineData(VideoAddress + "?fixture=one https://www.bilibili.com/bangumi/play/ep1002")]
    [InlineData(VideoAddress + " https://evil.invalid/other")]
    [InlineData(VideoAddress + "?fixture=one " + VideoAddress + "?fixture=two")]
    [InlineData(VideoAddress + " https://www.bilibili.com/bangumi/play/ss2001")]
    [InlineData("ftp://other.invalid/fixture " + VideoAddress)]
    public void FailedOrAmbiguousInputPreservesHomeTextAndDoesNotNavigate(string text)
    {
        using var settings = new TestSettingsStore();
        var navigation = new TestNavigationService();
        var search = new SearchService(settings.Store, navigation);
        var interactions = new TestDesktopInteractionContext(navigation);
        var notifications = 0;
        interactions.Notifications.NotificationRaised += (_, _) => notifications++;
        using var index = new IndexViewModel(interactions, new NoSession(), settings.Store, search,
            NullLogger<IndexViewModel>.Instance)
        { InputText = text };

        Assert.False(search.BiliInput(text, AppRoute.Index));
        index.InputCommand.Execute(null);

        Assert.Equal(text, index.InputText);
        Assert.Empty(navigation.Requests);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public void RepeatedIdenticalLinksNavigateOnlyOnce()
    {
        using var settings = new TestSettingsStore();
        var navigation = new TestNavigationService();
        var search = new SearchService(settings.Store, navigation);

        Assert.True(search.BiliInput($"【fixture】{VideoAddress} again {VideoAddress}", AppRoute.Index));

        AssertVideoRequest(new BilibiliBvidInput("BV17x411w7KC"), navigation, suppressDuplicate: false);
    }

    [Theory]
    [InlineData("https://www.bilibili.com/list/42?fixture=a b&sid=99")]
    [InlineData("\t https://www.bilibili.com/list/42?fixture=a b&sid=99 \t")]
    [InlineData("https://www.bilibili.com/list/42?sid=99&fixture=a b")]
    public void AcceptedWholeUriKeepsQueryIdentityAfterLinkDisambiguation(string text)
    {
        using var settings = new TestSettingsStore();
        var expected = Assert.IsType<BilibiliSeriesInput>(BilibiliInputParser.Parse(text));
        Assert.Equal(99, expected.SeriesId);
        var navigation = new TestNavigationService();
        var search = new SearchService(settings.Store, navigation);
        using var index = CreateIndex(settings.Store, navigation, search);
        index.InputText = text;

        index.InputCommand.Execute(null);

        Assert.Equal(string.Empty, index.InputText);
        var request = Assert.Single(navigation.Requests);
        Assert.Equal(AppRoute.SeasonsSeries, request.Route);
        Assert.Equal(new SeriesNavigationPayload(42, 99), request.Parameter);
    }

    [Fact]
    public void LongInputAndLateAmbiguityAreProcessedWithoutDroppingCandidates()
    {
        using var settings = new TestSettingsStore();
        var navigation = new TestNavigationService();
        var search = new SearchService(settings.Store, navigation);
        using var index = CreateIndex(settings.Store, navigation, search);
        index.InputText = VideoAddress + "?fixture=" + new string('a', 8192);
        index.InputCommand.Execute(null);
        AssertVideoRequest(new BilibiliBvidInput("BV17x411w7KC"), navigation, suppressDuplicate: false);
        Assert.Equal(string.Empty, index.InputText);
        navigation.Requests.Clear();

        var repeated = string.Concat(Enumerable.Repeat(VideoAddress + " ", 512));
        index.InputText = repeated + "https://www.bilibili.com/bangumi/play/ep1002";
        var ambiguous = index.InputText;
        index.InputCommand.Execute(null);
        Assert.Equal(ambiguous, index.InputText);
        Assert.Empty(navigation.Requests);

        index.InputText = new string('a', 8192);
        var unsupported = index.InputText;
        index.InputCommand.Execute(null);
        Assert.Equal(unsupported, index.InputText);
        Assert.Empty(navigation.Requests);
    }

    private static IndexViewModel CreateIndex(ISettingsStore settings, TestNavigationService navigation, SearchService search)
    {
        return new IndexViewModel(new TestDesktopInteractionContext(navigation), new NoSession(), settings, search,
            NullLogger<IndexViewModel>.Instance);
    }

    private static void AssertVideoRequest(BilibiliMediaInput expected, TestNavigationService navigation, bool suppressDuplicate)
    {
        var request = Assert.Single(navigation.Requests);
        Assert.Equal(AppRoute.VideoDetail, request.Route);
        Assert.Equal(AppRoute.Index, request.Parent);
        var payload = Assert.IsType<VideoNavigationPayload>(request.Parameter);
        Assert.Equal(expected, payload.Input);
        Assert.Equal(suppressDuplicate, payload.SuppressDuplicate);
    }

    private sealed class NoSession : IUserSessionCoordinator
    {
        public Task<UserSessionSnapshot> RefreshAsync(CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Input handling must not refresh the account.");
        }
    }
}
