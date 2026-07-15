using DownKyi.Application.Desktop;
using DownKyi.Core.BiliApi.Users.Models;
using DownKyi.Services.Account;
using DownKyi.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Prism.Events;

namespace DownKyi.Tests;

public sealed class UserSessionCoordinatorTests
{
    [Fact]
    public void ConstructingIndexViewModelDoesNotStartAccountWork()
    {
        using var settings = new TestSettingsStore();
        var coordinator = new RecordingUserSessionCoordinator();
        using var viewModel = new ViewIndexViewModel(
            new EventAggregator(),
            new StubNavigationService(),
            coordinator,
            settings.Store,
            NullLogger<ViewIndexViewModel>.Instance);

        Assert.Equal(0, coordinator.RefreshCount);
    }

    [Fact]
    public async Task RefreshPreservesCancellationBeforeNetworkWork()
    {
        using var settings = new TestSettingsStore();
        var coordinator = new UserSessionCoordinator(settings.Store);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => coordinator.RefreshAsync(cancellation.Token));
    }

    [Fact]
    public void MissingUserMapsToLoggedOutSettings()
    {
        var settings = UserSessionCoordinator.MapSettings(null);

        Assert.Equal(-1, settings.Mid);
        Assert.Equal(string.Empty, settings.Name);
        Assert.False(settings.IsLogin);
        Assert.False(settings.IsVip);
    }

    [Fact]
    public void NavigationUserMapsStableWbiKeys()
    {
        var settings = UserSessionCoordinator.MapSettings(new UserInfoForNavigation
        {
            Mid = 42,
            Name = "test-user",
            IsLogin = true,
            VipStatus = 1,
            Wbi = new Wbi
            {
                ImageAddress = "https://i.example.test/path/image-key.png?query=ignored",
                SubAddress = "//i.example.test/path/sub-key.jpg"
            }
        });

        Assert.Equal(42, settings.Mid);
        Assert.Equal("test-user", settings.Name);
        Assert.True(settings.IsLogin);
        Assert.True(settings.IsVip);
        Assert.Equal("image-key", settings.ImgKey);
        Assert.Equal("sub-key", settings.SubKey);
    }

    private sealed class RecordingUserSessionCoordinator : IUserSessionCoordinator
    {
        public int RefreshCount { get; private set; }

        public Task<UserSessionSnapshot> RefreshAsync(CancellationToken cancellationToken)
        {
            RefreshCount++;
            return Task.FromResult(new UserSessionSnapshot(null, false));
        }
    }

    private sealed class StubNavigationService : IAppNavigationService
    {
        public void Navigate(AppNavigationRequest request)
        {
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
}
