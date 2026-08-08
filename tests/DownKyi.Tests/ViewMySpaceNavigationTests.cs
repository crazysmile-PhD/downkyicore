using DownKyi.Application.Desktop;
using DownKyi.Core.BiliApi.Users.Models;
using DownKyi.Services.UserSpace;
using DownKyi.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class ViewMySpaceNavigationTests : IDisposable
{
    private readonly TestSettingsStore _settings = new();

    [Theory]
    [InlineData(0, AppRoute.MyFavorites)]
    [InlineData(1, AppRoute.MyBangumiFollow)]
    [InlineData(2, AppRoute.MyToViewVideo)]
    [InlineData(3, AppRoute.MyHistory)]
    public async Task InternalPackageUsesTypedNavigationAndResetsSelection(
        int selectedPackage,
        AppRoute expectedRoute)
    {
        var navigation = new RecordingNavigationService();
        var launcher = new RecordingPlatformLauncher();
        using var viewModel = CreateViewModel(navigation, launcher);
        viewModel.SelectedPackage = selectedPackage;

        await viewModel.PackageListCommand.ExecuteAsync(null);

        var request = Assert.Single(navigation.Requests);
        Assert.Equal(expectedRoute, request.Route);
        Assert.Equal(AppRoute.MySpace, request.Parent);
        Assert.Empty(launcher.OpenedUris);
        Assert.Equal(-1, viewModel.SelectedPackage);
    }

    [Fact]
    public async Task DynamicsPackageOpensOfficialPageWithoutInternalNavigationAndCanRepeat()
    {
        var navigation = new RecordingNavigationService();
        var launcher = new RecordingPlatformLauncher();
        using var viewModel = CreateViewModel(navigation, launcher);

        viewModel.SelectedPackage = 4;
        await viewModel.PackageListCommand.ExecuteAsync(null);

        Assert.Equal(new Uri("https://t.bilibili.com/"), Assert.Single(launcher.OpenedUris));
        Assert.Empty(navigation.Requests);
        Assert.Equal(-1, viewModel.SelectedPackage);

        viewModel.SelectedPackage = 4;
        await viewModel.PackageListCommand.ExecuteAsync(null);

        Assert.Equal(2, launcher.OpenedUris.Count);
        Assert.Empty(navigation.Requests);
        Assert.Equal(-1, viewModel.SelectedPackage);
    }

    [Fact]
    public async Task InvalidPackageDoesNothingAndResetsSelection()
    {
        var navigation = new RecordingNavigationService();
        var launcher = new RecordingPlatformLauncher();
        using var viewModel = CreateViewModel(navigation, launcher);
        viewModel.SelectedPackage = 99;

        await viewModel.PackageListCommand.ExecuteAsync(null);

        Assert.Empty(navigation.Requests);
        Assert.Empty(launcher.OpenedUris);
        Assert.Equal(-1, viewModel.SelectedPackage);
    }

    public void Dispose()
    {
        _settings.Dispose();
    }

    private ViewMySpaceViewModel CreateViewModel(
        IAppNavigationService navigation,
        IPlatformLauncher launcher)
    {
        return new ViewMySpaceViewModel(
            new TestDesktopInteractionContext(navigation),
            new StubUserSpacePageCoordinator(),
            launcher,
            _settings.Store,
            NullLogger<ViewMySpaceViewModel>.Instance);
    }

    private sealed class RecordingPlatformLauncher : IPlatformLauncher
    {
        public List<Uri> OpenedUris { get; } = [];

        public Task<bool> OpenFileAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> OpenFolderAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> OpenUriAsync(Uri uri, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenedUris.Add(uri);
            return Task.FromResult(true);
        }
    }

    private sealed class RecordingNavigationService : IAppNavigationService
    {
        public List<AppNavigationRequest> Requests { get; } = [];

        public event EventHandler<AppNavigationChangedEventArgs>? NavigationChanged
        {
            add { }
            remove { }
        }

        public void Navigate(AppNavigationRequest request) => Requests.Add(request);

        public void NavigateRegion(
            AppNavigationRegion region,
            AppRoute route,
            IReadOnlyDictionary<string, object?>? parameters = null)
        {
        }

        public void ClearRegion(AppNavigationRegion region)
        {
        }

        public object? GetActiveView(AppNavigationRegion region) => null;

        public bool CanGoBack(AppNavigationRegion region) => false;

        public void GoBack(AppNavigationRegion region)
        {
        }
    }

    private sealed class StubUserSpacePageCoordinator : IUserSpacePageCoordinator
    {
        public Task<PublicationPageSnapshot> LoadPublicationPageAsync(
            long mid,
            int page,
            int pageSize,
            long typeId,
            string? keyword,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MySpaceProfileSnapshot?> LoadMyProfileAsync(
            long mid,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MySpaceStatsSnapshot> LoadMyStatsAsync(
            long mid,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<BangumiFollowPageSnapshot> LoadBangumiFollowPageAsync(
            long mid,
            BangumiType type,
            int page,
            int pageSize,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
