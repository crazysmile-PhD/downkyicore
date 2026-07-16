using DownKyi.Application.Desktop;

namespace DownKyi.Tests;

internal sealed class TestNavigationService : IAppNavigationService
{
    public List<AppNavigationRequest> Requests { get; } = [];

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
    }

    public void ClearRegion(AppNavigationRegion region)
    {
    }

    public object? GetActiveView(AppNavigationRegion region)
    {
        return null;
    }
}
