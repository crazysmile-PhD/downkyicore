using DownKyi.Application.Desktop;
using DownKyi.Platform;
using DownKyi.Services;

namespace DownKyi.Tests;

public sealed class DesktopInteractionServiceTests
{
    [Fact]
    public void NotificationServicePublishesOneTypedEvent()
    {
        var service = new DesktopNotificationService();
        UserNotificationEventArgs? received = null;
        service.NotificationRaised += (_, args) => received = args;

        service.Show("download queued");

        Assert.NotNull(received);
        Assert.Equal("download queued", received.Message);
        Assert.Throws<ArgumentException>(() => service.Show(string.Empty));
    }

    [Fact]
    public void EveryTypedRouteMapsToOneNonEmptyLegacyRoute()
    {
        var routeNames = Enum.GetValues<AppRoute>()
            .Select(PrismNavigationService.GetRouteName)
            .ToArray();

        Assert.DoesNotContain(routeNames, string.IsNullOrWhiteSpace);
        Assert.Equal(routeNames.Length, routeNames.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryTypedRegionMapsToOneNonEmptyLegacyRegion()
    {
        var regionNames = Enum.GetValues<AppNavigationRegion>()
            .Select(PrismNavigationService.GetRegionName)
            .ToArray();

        Assert.DoesNotContain(regionNames, string.IsNullOrWhiteSpace);
        Assert.Equal(regionNames.Length, regionNames.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryTypedDialogMapsToOneNonEmptyLegacyDialog()
    {
        var dialogNames = Enum.GetValues<AppDialog>()
            .Select(PrismDialogService.GetDialogName)
            .ToArray();

        Assert.DoesNotContain(dialogNames, string.IsNullOrWhiteSpace);
        Assert.Equal(dialogNames.Length, dialogNames.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(42, AppRoute.MySpace)]
    [InlineData(43, AppRoute.UserSpace)]
    public void SearchUsesTypedUserSpaceRoutes(long targetMid, AppRoute expectedRoute)
    {
        using var settings = new TestSettingsStore();
        settings.Store.Update(current => current with
        {
            User = current.User with { Mid = 42, IsLogin = true, Name = "test-user" }
        });
        var navigation = new RecordingNavigationService();
        var search = new SearchService(settings.Store, navigation);

        Assert.True(search.BiliInput($"uid:{targetMid}", AppRoute.Index));

        var request = Assert.Single(navigation.Requests);
        Assert.Equal(expectedRoute, request.Route);
        Assert.Equal(AppRoute.Index, request.Parent);
        Assert.Equal(targetMid, request.Parameter);
    }

    [Fact]
    public void UnsupportedSearchInputDoesNotNavigate()
    {
        using var settings = new TestSettingsStore();
        var navigation = new RecordingNavigationService();
        var search = new SearchService(settings.Store, navigation);

        Assert.False(search.BiliInput("not-a-supported-input", AppRoute.Index));
        Assert.Empty(navigation.Requests);
    }

    private sealed class RecordingNavigationService : IAppNavigationService
    {
        public List<AppNavigationRequest> Requests { get; } = [];

        public void Navigate(AppNavigationRequest request)
        {
            Requests.Add(request);
        }

        public void NavigateRegion(
            AppNavigationRegion region,
            AppRoute route,
            IReadOnlyDictionary<string, object?>? parameters = null)
        {
            throw new NotSupportedException();
        }

        public void ClearRegion(AppNavigationRegion region)
        {
            throw new NotSupportedException();
        }

        public object? GetActiveView(AppNavigationRegion region)
        {
            throw new NotSupportedException();
        }
    }
}
