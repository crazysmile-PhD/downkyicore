using DownKyi.Events;
using DownKyi.Services;
using DownKyi.ViewModels;
using Prism.Events;

namespace DownKyi.Tests;

#pragma warning disable CA1515 // xUnit requires public test classes.
public sealed class UserVideoListNavigationTests
{
    [Fact]
    public void ReturningFromAChildPageRestoresTheCurrentNumericListState()
    {
        Assert.True(ViewPublicationViewModel.ShouldRestoreListState(
            preserveStateOnReturn: true,
            currentMid: 3546801722362343,
            currentIsUserVideoList: true,
            incomingMid: 3546801722362343,
            incomingIsUserVideoList: true,
            loadedTabCount: 1));

        Assert.False(ViewPublicationViewModel.ShouldRestoreListState(
            preserveStateOnReturn: true,
            currentMid: 3546801722362343,
            currentIsUserVideoList: true,
            incomingMid: 42,
            incomingIsUserVideoList: true,
            loadedTabCount: 1));

        Assert.False(ViewPublicationViewModel.ShouldRestoreListState(
            preserveStateOnReturn: false,
            currentMid: 3546801722362343,
            currentIsUserVideoList: true,
            incomingMid: 3546801722362343,
            incomingIsUserVideoList: true,
            loadedTabCount: 1));
    }

    [Fact]
    public void NumericUserVideoSearchFiltersTitlesCaseInsensitively()
    {
        var videos = new[]
        {
            new DownKyi.Core.BiliApi.Users.Models.UserVideoListArchive { Title = "Aespa Lemonade" },
            new DownKyi.Core.BiliApi.Users.Models.UserVideoListArchive { Title = "其他视频" },
            new DownKyi.Core.BiliApi.Users.Models.UserVideoListArchive { Title = "AESPA Drama" }
        };

        var result = ViewPublicationViewModel.FilterUserVideoList(videos, " aespa ");

        Assert.Equal(2, result.Count);
        Assert.All(result, video =>
            Assert.Contains("aespa", video.Title, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NumericListUrlNavigatesToTheUploaderVideoList()
    {
        var eventAggregator = new EventAggregator();
        NavigationParam? navigation = null;
        eventAggregator.GetEvent<NavigationEvent>().Subscribe(value => navigation = value);
        var service = new SearchService(eventAggregator);

        var handled = service.BiliInput(
            "https://www.bilibili.com/list/3546801722362343",
            ViewIndexViewModel.Tag);

        Assert.True(handled);
        Assert.NotNull(navigation);
        Assert.Equal(ViewPublicationViewModel.Tag, navigation.ViewName);
        Assert.Equal(ViewIndexViewModel.Tag, navigation.ParentViewName);
        var data = Assert.IsType<Dictionary<string, object>>(navigation.Parameter);
        Assert.Equal(3546801722362343, data["mid"]);
        Assert.Equal(true, data["userVideoList"]);
    }
}
#pragma warning restore CA1515
