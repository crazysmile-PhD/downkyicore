using DownKyi.Events;
using DownKyi.Services;
using DownKyi.ViewModels;
using Prism.Events;

namespace DownKyi.Tests;

#pragma warning disable CA1515 // xUnit requires public test classes.
public sealed class UserVideoListNavigationTests
{
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
