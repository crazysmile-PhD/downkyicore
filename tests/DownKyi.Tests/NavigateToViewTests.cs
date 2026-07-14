using DownKyi.Core.Settings.Models;
using DownKyi.Events;
using DownKyi.Utils;
using DownKyi.ViewModels;
using Prism.Events;

namespace DownKyi.Tests;

public sealed class NavigateToViewTests
{
    [Theory]
    [InlineData(42, ViewMySpaceViewModel.Tag)]
    [InlineData(43, ViewUserSpaceViewModel.Tag)]
    public async Task UserSpaceNavigationUsesTheInjectedSignedInUser(long targetMid, string expectedView)
    {
        using var settings = new TestSettingsStore();
        settings.Store.Settings.SetUserInfo(new UserInfoSettings
        {
            Mid = 42,
            IsLogin = true,
            Name = "test-user"
        });

        var eventAggregator = new EventAggregator();
        NavigationParam? navigation = null;
        eventAggregator.GetEvent<NavigationEvent>().Subscribe(value => navigation = value);

        NavigateToView.NavigateToViewUserSpace(eventAggregator, settings.Store, "parent", targetMid);

        Assert.NotNull(navigation);
        Assert.Equal(expectedView, navigation.ViewName);
        Assert.Equal("parent", navigation.ParentViewName);
        Assert.Equal(targetMid, navigation.Parameter);

        await settings.Store.FlushAsync(TestContext.Current.CancellationToken);
    }
}
