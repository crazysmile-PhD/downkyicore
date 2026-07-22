using DownKyi.Application.Desktop;
using DownKyi.ViewModels;

namespace DownKyi.Tests;

public sealed class BackNavigationTests
{
    [Fact]
    public void BackUsesHistoryWithoutAddingAForwardRequest()
    {
        var navigation = new TestNavigationService { CanGoBackResult = true };
        using var viewModel = new BackProbeViewModel(new TestDesktopInteractionContext(navigation));
        viewModel.OnNavigatedTo(CreateContext(AppRoute.Settings));

        viewModel.GoBack();

        Assert.Equal(AppNavigationRegion.Main, Assert.Single(navigation.BackRequests));
        Assert.Empty(navigation.Requests);
    }

    [Fact]
    public void BackFallsBackToParentRouteWhenHistoryIsEmpty()
    {
        var navigation = new TestNavigationService();
        using var viewModel = new BackProbeViewModel(new TestDesktopInteractionContext(navigation));
        viewModel.OnNavigatedTo(CreateContext(AppRoute.Settings));

        viewModel.GoBack();

        Assert.Empty(navigation.BackRequests);
        Assert.Equal(
            new AppNavigationRequest(AppRoute.Settings),
            Assert.Single(navigation.Requests));
    }

    private static AppNavigationContext CreateContext(AppRoute parentRoute)
    {
        return new AppNavigationContext(
            AppNavigationRegion.Main,
            AppRoute.Toolbox,
            parentRoute,
            null,
            new AppNavigationParameters());
    }

    private sealed class BackProbeViewModel(IDesktopInteractionContext desktopInteractions)
        : ViewModelBase(desktopInteractions)
    {
        public void GoBack()
        {
            if (TryNavigateBack())
            {
                return;
            }

            NavigateToParent();
        }
    }
}
