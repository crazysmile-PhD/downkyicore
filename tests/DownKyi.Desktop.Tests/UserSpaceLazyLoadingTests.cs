using Avalonia.Controls;
using Avalonia.Threading;
using DownKyi.Application.Desktop;
using DownKyi.Core.BiliApi.Users.Models;
using DownKyi.Infrastructure.Settings;
using DownKyi.Platform;
using DownKyi.Services.UserSpace;
using DownKyi.ViewModels;
using DownKyi.ViewModels.UserSpace;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Desktop.Tests;

public sealed class UserSpaceLazyLoadingTests
{
    private static readonly int[] ExpectedTabs = [0, 1, 2, 3];

    [AvaloniaFact]
    public async Task ReturningThroughAnotherUserRestoresTheOriginalUserTabAndPage()
    {
        using var fixture = new Fixture();
        fixture.Navigation.Navigate(new AppNavigationRequest(AppRoute.UserSpace, AppRoute.Index, 42L));
        fixture.Coordinator.Overview.SetResult(new UserSpaceOverviewSnapshot([], null, null));
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
        fixture.Parent.TabLeftBannersCommand.Execute(fixture.Parent.TabLeftBanners.Single(tab => tab.Id == 3));
        fixture.Coordinator.FolderRequests[0].Response.SetResult(new UserSpaceFavoriteFolderPage([Folder(1, "first")], 3));
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
        var original = Assert.IsType<FavoritesViewModel>(fixture.Navigation.GetActiveView(AppNavigationRegion.UserSpace));
        original.Pager.Current = 2;
        fixture.Coordinator.FolderRequests[1].Response.SetResult(new UserSpaceFavoriteFolderPage([Folder(2, "second")], 3));
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);

        fixture.Navigation.Navigate(new AppNavigationRequest(AppRoute.UserSpace, AppRoute.Index, 43L));
        fixture.Parent.TabLeftBannersCommand.Execute(fixture.Parent.TabLeftBanners.Single(tab => tab.Id == 3));
        var other = fixture.Coordinator.FolderRequests[2];
        fixture.Navigation.GoBack(AppNavigationRegion.Main);

        var restored = Assert.IsType<FavoritesViewModel>(fixture.Navigation.GetActiveView(AppNavigationRegion.UserSpace));
        var resumed = fixture.Coordinator.FolderRequests[3];
        Assert.Equal(42, restored.Mid);
        Assert.Equal(2, restored.Page);
        Assert.Equal(42, resumed.Mid);
        Assert.True(other.Token.IsCancellationRequested);
        resumed.Response.SetResult(new UserSpaceFavoriteFolderPage([Folder(3, "restored")], 3));
        other.Response.SetResult(new UserSpaceFavoriteFolderPage([Folder(4, "other user")], 3));
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
        Assert.Equal("restored", Assert.Single(restored.Favorites).Title);
    }
    [AvaloniaFact]
    public async Task HeaderIsVisibleBeforeOverviewAndRetainedNavigationCancelsSectionReads()
    {
        using var fixture = new Fixture();
        fixture.Navigation.Navigate(new AppNavigationRequest(AppRoute.UserSpace, AppRoute.Index, 42L));

        Assert.Equal("fixture", fixture.Parent.UserName);
        Assert.True(fixture.Parent.ViewVisibility);
        Assert.Empty(fixture.Coordinator.FolderRequests);
        Assert.Equal(0, fixture.Coordinator.CollectionRequests);
        fixture.Coordinator.Overview.SetResult(new UserSpaceOverviewSnapshot([], null, null));
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
        Assert.Equal(ExpectedTabs, fixture.Parent.TabLeftBanners.Select(tab => tab.Id));
        Assert.Empty(fixture.Coordinator.FolderRequests);
        Assert.Equal(0, fixture.Coordinator.CollectionRequests);

        fixture.Parent.TabLeftBannersCommand.Execute(fixture.Parent.TabLeftBanners.Single(tab => tab.Id == 3));
        var first = Assert.Single(fixture.Coordinator.FolderRequests);
        var oldPage = Assert.IsType<FavoritesViewModel>(fixture.Navigation.GetActiveView(AppNavigationRegion.UserSpace));
        fixture.Navigation.Navigate(new AppNavigationRequest(AppRoute.Toolbox));
        Assert.True(first.Token.IsCancellationRequested);
        fixture.Navigation.GoBack(AppNavigationRegion.Main);

        Assert.Equal(1, fixture.Coordinator.ProfileRequests);
        Assert.Equal(2, fixture.Coordinator.FolderRequests.Count);
        var current = Assert.IsType<FavoritesViewModel>(fixture.Navigation.GetActiveView(AppNavigationRegion.UserSpace));
        Assert.Same(oldPage, current);
        first.Response.SetResult(new UserSpaceFavoriteFolderPage([Folder(1, "stale")], 3));
        fixture.Coordinator.FolderRequests[1].Response.SetResult(new UserSpaceFavoriteFolderPage([Folder(2, "current")], 3));
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
        Assert.Equal("current", Assert.Single(current.Favorites).Title);

        current.Pager.Current = 2;
        Assert.Equal(2, fixture.Coordinator.FolderRequests[2].Page);
        fixture.Coordinator.FolderRequests[2].Response.SetResult(new UserSpaceFavoriteFolderPage([Folder(3, "second page")], 3));
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
        fixture.Navigation.Navigate(new AppNavigationRequest(AppRoute.Toolbox));
        fixture.Navigation.GoBack(AppNavigationRegion.Main);
        Assert.Same(current, fixture.Navigation.GetActiveView(AppNavigationRegion.UserSpace));
        Assert.Equal(2, current.Pager.Current);
        Assert.Equal(3, fixture.Coordinator.FolderRequests.Count);
        Assert.Equal(50, fixture.Coordinator.FolderRequests[2].PageSize);
    }

    [AvaloniaFact]
    public async Task FailedSectionShowsRetryAndSuccessfulEmptyPageShowsEmptyState()
    {
        using var fixture = new Fixture();
        fixture.Navigation.Navigate(new AppNavigationRequest(AppRoute.UserSpace, AppRoute.Index, 42L));
        fixture.Coordinator.Overview.SetResult(new UserSpaceOverviewSnapshot([], null, null));
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
        fixture.Parent.TabLeftBannersCommand.Execute(fixture.Parent.TabLeftBanners.Single(tab => tab.Id == 3));
        var page = Assert.IsType<FavoritesViewModel>(fixture.Navigation.GetActiveView(AppNavigationRegion.UserSpace));
        var view = new DownKyi.Views.UserSpace.ViewFavorites { DataContext = page };
        var window = new Window { Content = view, Width = 900, Height = 550 };
        try
        {
            window.Show();
            fixture.Coordinator.FolderRequests[0].Response.SetException(new HttpRequestException("Fixture failure."));
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(page.HasError);
            Assert.False(page.IsEmpty);
            Assert.True(view.FindControl<StackPanel>("SectionError")!.IsVisible);
            Assert.False(view.FindControl<ContentControl>("SectionEmpty")!.IsVisible);

            page.RetryCommand.Execute(null);
            fixture.Coordinator.FolderRequests[1].Response.SetResult(new UserSpaceFavoriteFolderPage([], 1));
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.False(page.HasError);
            Assert.True(page.IsEmpty);
            Assert.False(view.FindControl<StackPanel>("SectionError")!.IsVisible);
            Assert.True(view.FindControl<ContentControl>("SectionEmpty")!.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task LateProfileForPreviousUserCannotReplaceCurrentUser()
    {
        using var fixture = new Fixture();
        var first = new TaskCompletionSource<UserSpaceProfileSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<UserSpaceProfileSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Coordinator.ProfileHandler = (mid, _) => mid == 42 ? first.Task : second.Task;
        fixture.Navigation.Navigate(new AppNavigationRequest(AppRoute.UserSpace, AppRoute.Index, 42L));
        fixture.Parent.OnNavigatedTo(new AppNavigationContext(AppNavigationRegion.Main, AppRoute.UserSpace,
            AppRoute.Index, 43L, new AppNavigationParameters(new Dictionary<string, object?> { ["Parameter"] = 43L })));
        second.SetResult(new UserSpaceProfileSnapshot(new UserInfoForSpace { Mid = 43, Name = "current", Level = 0 }, ""));
        fixture.Coordinator.Overview.SetResult(new UserSpaceOverviewSnapshot([], null, null));
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
        first.SetResult(new UserSpaceProfileSnapshot(new UserInfoForSpace { Mid = 42, Name = "stale", Level = 0 }, ""));
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
        Assert.Equal("current", fixture.Parent.UserName);
    }

    private static UserSpaceFavoriteFolder Folder(long id, string title) => new(id,
        "avares://DownKyi.Desktop/Resources/video-placeholder.png", title, 1, 0);

    private sealed class Fixture : IDisposable
    {
        private readonly SettingsStore _settings = new(Path.Combine(Path.GetTempPath(), $"downkyi-lazy-space-{Guid.NewGuid():N}.json"));
        public ControlledCoordinator Coordinator { get; } = new();
        public AvaloniaNavigationService Navigation { get; }
        public UserSpaceViewModel Parent => (UserSpaceViewModel)Navigation.GetActiveView(AppNavigationRegion.Main)!;
        public Fixture()
        {
            DesktopTestResources.EnsureProductThemeResources();
            InteractionStub? interactions = null;
            Navigation = new AvaloniaNavigationService(route => route switch
            {
                AppRoute.UserSpace => new UserSpaceViewModel(interactions!, _settings, Coordinator, NullLogger<UserSpaceViewModel>.Instance),
                AppRoute.Archive => new ArchiveViewModel(interactions!),
                AppRoute.UserSpaceFavorites => new FavoritesViewModel(interactions!, Coordinator, NullLogger<FavoritesViewModel>.Instance),
                AppRoute.UserSpaceSeasonsSeries => new UserSpaceSeasonsSeriesViewModel(interactions!, Coordinator, NullLogger<UserSpaceSeasonsSeriesViewModel>.Instance),
                _ => new object()
            }, action => action());
            interactions = new InteractionStub(Navigation);
        }
        public void Dispose()
        {
            Navigation.Dispose();
            _settings.Dispose();
            Coordinator.Overview.TrySetResult(new UserSpaceOverviewSnapshot([], null, null));
            foreach (var request in Coordinator.FolderRequests)
                request.Response.TrySetResult(new UserSpaceFavoriteFolderPage([], 1));
        }
    }

    private sealed class ControlledCoordinator : IUserSpaceLoadCoordinator
    {
        public int ProfileRequests { get; private set; }
        public int CollectionRequests { get; private set; }
        public Func<long, CancellationToken, Task<UserSpaceProfileSnapshot>> ProfileHandler { get; set; } =
            (mid, _) => Task.FromResult(new UserSpaceProfileSnapshot(new UserInfoForSpace { Mid = mid, Name = "fixture", Level = 0 }, ""));
        public TaskCompletionSource<UserSpaceOverviewSnapshot> Overview { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<FolderRequest> FolderRequests { get; } = [];
        public Task<UserSpaceProfileSnapshot> LoadProfileAsync(long mid, CancellationToken cancellationToken)
        { ProfileRequests++; return ProfileHandler(mid, cancellationToken); }
        public Task<UserSpaceOverviewSnapshot> LoadOverviewAsync(long mid, CancellationToken cancellationToken) => Overview.Task;
        public Task<UserSpaceFavoriteFolderPage> LoadFavoriteFoldersPageAsync(long mid, int page, int pageSize, CancellationToken cancellationToken)
        {
            var request = new FolderRequest(mid, page, pageSize,
                new TaskCompletionSource<UserSpaceFavoriteFolderPage>(TaskCreationOptions.RunContinuationsAsynchronously), cancellationToken);
            FolderRequests.Add(request);
            return request.Response.Task;
        }
        public Task<UserSpaceSeasonsPage> LoadSeasonsSeriesPageAsync(long mid, int page, int pageSize, CancellationToken cancellationToken)
        { CollectionRequests++; return Task.FromResult(new UserSpaceSeasonsPage(new SpaceSeasonsSeries(), 1)); }
    }
    private sealed record FolderRequest(long Mid, int Page, int PageSize, TaskCompletionSource<UserSpaceFavoriteFolderPage> Response, CancellationToken Token);
    private sealed class InteractionStub(IAppNavigationService navigation) : IDesktopInteractionContext
    {
        public IAppNavigationService Navigation => navigation;
        public IUserNotificationService Notifications { get; } = new NotificationStub();
        public IAppDialogService Dialogs => null!;
    }
    private sealed class NotificationStub : IUserNotificationService
    {
        public event EventHandler<UserNotificationEventArgs>? NotificationRaised { add { } remove { } }
        public void Show(string message) { }
    }
}
