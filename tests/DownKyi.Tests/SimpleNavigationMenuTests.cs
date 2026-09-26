using DownKyi.Application.Desktop;
using DownKyi.ViewModels;

namespace DownKyi.Tests;

public sealed class SimpleNavigationMenuTests
{
    [Theory]
    [InlineData(0, 0L, AppRoute.SettingsBasic)]
    [InlineData(1, 1L, AppRoute.SettingsNetwork)]
    [InlineData(2, 2L, AppRoute.SettingsVideo)]
    [InlineData(3, 3L, AppRoute.SettingsDanmaku)]
    [InlineData(4, 4L, AppRoute.SettingsAbout)]
    public void SettingsMenuItemNavigatesToExpectedRoute(
        int menuIndex,
        long expectedMenuId,
        AppRoute expectedRoute)
    {
        var navigation = new TestNavigationService();
        using var viewModel = new ViewSettingsViewModel(new TestDesktopInteractionContext(navigation));
        Assert.Equal(5, viewModel.TabHeaders.Count);
        var menuItem = viewModel.TabHeaders[menuIndex];
        Assert.Equal(expectedMenuId, menuItem.Id);

        viewModel.LeftTabHeadersCommand.Execute(menuItem);

        Assert.Equal(
            (AppNavigationRegion.Settings, expectedRoute),
            Assert.Single(navigation.RegionRequests));
    }

    [Theory]
    [InlineData(0, 0L, AppRoute.Downloading)]
    [InlineData(1, 1L, AppRoute.DownloadFinished)]
    public void DownloadManagerMenuItemNavigatesToExpectedRoute(
        int menuIndex,
        long expectedMenuId,
        AppRoute expectedRoute)
    {
        var navigation = new TestNavigationService();
        using var viewModel = new ViewDownloadManagerViewModel(new TestDesktopInteractionContext(navigation));
        Assert.Equal(2, viewModel.TabHeaders.Count);
        var menuItem = viewModel.TabHeaders[menuIndex];
        Assert.Equal(expectedMenuId, menuItem.Id);

        viewModel.LeftTabHeadersCommand.Execute(menuItem);

        Assert.Equal(
            (AppNavigationRegion.DownloadManager, expectedRoute),
            Assert.Single(navigation.RegionRequests));
    }

    [Theory]
    [InlineData(0, 0L, AppRoute.BiliHelper)]
    [InlineData(1, 1L, AppRoute.Delogo)]
    [InlineData(2, 2L, AppRoute.ExtractMedia)]
    public void ToolboxMenuItemNavigatesToExpectedRoute(
        int menuIndex,
        long expectedMenuId,
        AppRoute expectedRoute)
    {
        var navigation = new TestNavigationService();
        using var viewModel = new ViewToolboxViewModel(new TestDesktopInteractionContext(navigation));
        Assert.Equal(3, viewModel.TabHeaders.Count);
        var menuItem = viewModel.TabHeaders[menuIndex];
        Assert.Equal(expectedMenuId, menuItem.Id);

        viewModel.LeftTabHeadersCommand.Execute(menuItem);

        Assert.Equal(
            (AppNavigationRegion.Toolbox, expectedRoute),
            Assert.Single(navigation.RegionRequests));
    }
}
