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
    public void EveryTypedRouteMapsToOneViewModelType()
    {
        var viewModelTypes = Enum.GetValues<AppRoute>()
            .Select(AvaloniaNavigationService.GetViewModelType)
            .ToArray();

        Assert.DoesNotContain(viewModelTypes, type => !type.Name.EndsWith("ViewModel", StringComparison.Ordinal));
        Assert.Equal(viewModelTypes.Length, viewModelTypes.Distinct().Count());
    }

    [Fact]
    public void EveryTypedRegionHasOneStableNumericIdentity()
    {
        var regionIds = Enum.GetValues<AppNavigationRegion>()
            .Select(region => (int)region)
            .ToArray();

        Assert.Equal(regionIds.Length, regionIds.Distinct().Count());
    }

    [Fact]
    public void EveryTypedDialogMapsToOneViewAndViewModelPair()
    {
        var dialogTypes = Enum.GetValues<AppDialog>()
            .Select(AvaloniaDialogService.GetDialogTypes)
            .ToArray();

        Assert.Equal(dialogTypes.Length, dialogTypes.Select(pair => pair.View).Distinct().Count());
        Assert.Equal(dialogTypes.Length, dialogTypes.Select(pair => pair.ViewModel).Distinct().Count());
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
    public void UserUploadVideoUrlNavigatesToUserSpaceHome()
    {
        using var settings = new TestSettingsStore();
        var navigation = new RecordingNavigationService();
        var search = new SearchService(settings.Store, navigation);

        Assert.True(search.BiliInput(
            "https://space.bilibili.com/3707029862484836/upload/video",
            AppRoute.Index));

        var request = Assert.Single(navigation.Requests);
        Assert.Equal(AppRoute.UserSpace, request.Route);
        Assert.Equal(AppRoute.Index, request.Parent);
        Assert.Equal(3707029862484836L, request.Parameter);
    }

    [Theory]
    [InlineData("https://space.bilibili.com/301479902")]
    [InlineData(" https://space.bilibili.com/301479902\r\n")]
    [InlineData("【UP主个人空间】 https://space.bilibili.com/301479902 ")]
    [InlineData("https://space.bilibili.com/301479902\r\n" + AppConstant.ClipboardId)]
    public void CopiedUserSpaceUrlNavigatesToUserSpaceHome(string input)
    {
        using var settings = new TestSettingsStore();
        var navigation = new RecordingNavigationService();
        var search = new SearchService(settings.Store, navigation);

        Assert.True(search.BiliInput(input, AppRoute.Index));

        var request = Assert.Single(navigation.Requests);
        Assert.Equal(AppRoute.UserSpace, request.Route);
        Assert.Equal(AppRoute.Index, request.Parent);
        Assert.Equal(301479902L, request.Parameter);
    }

    [Fact]
    public void ClipboardComInitializationFailureIsRecoverable()
    {
        var exception = System.Runtime.InteropServices.Marshal.GetExceptionForHR(
            unchecked((int)0x800401F0));

        Assert.NotNull(exception);
        Assert.True(App.IsClipboardComInitializationFailure(exception));
        Assert.False(App.IsClipboardComInitializationFailure(new InvalidOperationException()));
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

    [Fact]
    public void NumericListUrlUsesTypedPublicationPayload()
    {
        using var settings = new TestSettingsStore();
        var navigation = new RecordingNavigationService();
        var search = new SearchService(settings.Store, navigation);

        Assert.True(search.BiliInput("https://www.bilibili.com/list/3546801722362343", AppRoute.Index));

        var request = Assert.Single(navigation.Requests);
        Assert.Equal(AppRoute.Publication, request.Route);
        Assert.Equal(AppRoute.Index, request.Parent);
        var payload = Assert.IsType<PublicationNavigationPayload>(request.Parameter);
        Assert.Equal(3546801722362343, payload.Mid);
        Assert.Equal(0, payload.SelectedTypeId);
        Assert.Empty(payload.Zones);
    }

    [Fact]
    public void SeriesListUrlDoesNotMasqueradeAsUploaderList()
    {
        using var settings = new TestSettingsStore();
        var navigation = new RecordingNavigationService();
        var search = new SearchService(settings.Store, navigation);

        Assert.False(search.BiliInput("https://www.bilibili.com/list/42?sid=99", AppRoute.Index));
        Assert.Empty(navigation.Requests);
    }

    private sealed class RecordingNavigationService : IAppNavigationService
    {
        public event EventHandler<AppNavigationChangedEventArgs>? NavigationChanged
        {
            add { }
            remove { }
        }

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

        public bool CanGoBack(AppNavigationRegion region)
        {
            return false;
        }

        public void GoBack(AppNavigationRegion region)
        {
            throw new NotSupportedException();
        }
    }
}
