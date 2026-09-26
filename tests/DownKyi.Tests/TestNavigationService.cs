using DownKyi.Application.Desktop;

namespace DownKyi.Tests;

internal sealed class TestNavigationService : IAppNavigationService
{
    public event EventHandler<AppNavigationChangedEventArgs>? NavigationChanged
    {
        add { }
        remove { }
    }

    public List<AppNavigationRequest> Requests { get; } = [];
    public List<(AppNavigationRegion Region, AppRoute Route)> RegionRequests { get; } = [];
    public List<AppNavigationRegion> BackRequests { get; } = [];
    public bool CanGoBackResult { get; set; }

    public void Navigate(AppNavigationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Requests.Add(request);
    }

    public void NavigateRegion(
        AppNavigationRegion region,
        AppRoute route,
        IReadOnlyDictionary<string, object?>? parameters = null)
    {
        RegionRequests.Add((region, route));
    }

    public void ClearRegion(AppNavigationRegion region)
    {
    }

    public object? GetActiveView(AppNavigationRegion region)
    {
        return null;
    }

    public bool CanGoBack(AppNavigationRegion region)
    {
        return CanGoBackResult;
    }

    public void GoBack(AppNavigationRegion region)
    {
        BackRequests.Add(region);
    }
}
