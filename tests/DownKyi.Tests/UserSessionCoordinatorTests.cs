using DownKyi.Core.BiliApi.Users.Models;
using DownKyi.Services.Account;
using DownKyi.ViewModels;
using Prism.Events;

namespace DownKyi.Tests;

public sealed class UserSessionCoordinatorTests
{
    [Fact]
    public void ConstructingIndexViewModelDoesNotStartAccountWork()
    {
        var coordinator = new RecordingUserSessionCoordinator();
        using var viewModel = new ViewIndexViewModel(new EventAggregator(), coordinator);

        Assert.Equal(0, coordinator.RefreshCount);
    }

    [Fact]
    public async Task RefreshPreservesCancellationBeforeNetworkWork()
    {
        var coordinator = new UserSessionCoordinator();
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
}
