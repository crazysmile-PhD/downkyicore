using Avalonia.Threading;
using DownKyi.Application.Desktop;
using DownKyi.Infrastructure.Settings;
using DownKyi.Platform;
using DownKyi.Presentation;
using DownKyi.Services.Media;
using DownKyi.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Desktop.Tests;

public sealed class PersonalMediaReturnTests
{
    [AvaloniaFact]
    public async Task HistoryReturnRetainsPagesRowsAndSelectionAndCanContinueFromCommittedCursor()
    {
        using var fixture = new Fixture();
        fixture.Enter(AppRoute.MyHistory);
        var page = fixture.History;
        fixture.CompleteHistory(0, 10, "first", "second");
        await DrainAsync().ConfigureAwait(true);
        page.LoadMoreCommand.Execute(null);
        fixture.CompleteHistory(1, 20, "third", "fourth");
        await DrainAsync().ConfigureAwait(true);
        var first = page.Medias[0];
        first.IsSelected = true;
        first.TitleCommand.Execute(null);
        fixture.Back();

        Assert.Same(page, fixture.History);
        Assert.Equal(4, page.Medias.Count);
        Assert.Same(first, page.Medias[0]);
        Assert.True(first.IsSelected);
        Assert.Equal(2, fixture.Coordinator.History.Count);
        page.LoadMoreCommand.Execute(null);
        var next = fixture.Coordinator.History[2];
        Assert.Equal(20, next.Max);
        Assert.Equal(120, next.ViewAt);
        Assert.False(next.Token.IsCancellationRequested);
        fixture.CompleteHistory(2, 30, "fifth");
        await DrainAsync().ConfigureAwait(true);
        Assert.Equal(5, page.Medias.Count);
    }

    [AvaloniaFact]
    public async Task WatchLaterReturnRetainsRowsAndSelectionButANewEntryLoadsAgain()
    {
        using var fixture = new Fixture();
        fixture.Enter(AppRoute.MyToViewVideo);
        var page = fixture.ToView;
        fixture.CompleteToView(0, "first");
        await DrainAsync().ConfigureAwait(true);
        var first = Assert.Single(page.Medias);
        first.IsSelected = true;
        page.IsSelectAll = true;
        first.TitleCommand.Execute(null);
        fixture.Back();

        Assert.Same(page, fixture.ToView);
        Assert.Same(first, Assert.Single(page.Medias));
        Assert.True(first.IsSelected);
        Assert.True(page.IsSelectAll);
        Assert.Single(fixture.Coordinator.ToView);

        fixture.Enter(AppRoute.MyToViewVideo);
        Assert.NotSame(page, fixture.ToView);
        Assert.Equal(2, fixture.Coordinator.ToView.Count);
        fixture.CompleteToView(1, "fresh");
        await DrainAsync().ConfigureAwait(true);
        Assert.Equal("fresh", Assert.Single(fixture.ToView.Medias).Title);
    }

    [AvaloniaFact]
    public async Task InterruptedHistoryPageResumesAtUncommittedCursorAndIgnoresLateResults()
    {
        using var fixture = new Fixture();
        fixture.Enter(AppRoute.MyHistory);
        fixture.CompleteHistory(0, 10, "committed");
        await DrainAsync().ConfigureAwait(true);
        var page = fixture.History;
        var first = Assert.Single(page.Medias);
        page.LoadMoreCommand.Execute(null);
        var interrupted = fixture.Coordinator.History[1];
        fixture.Away();
        Assert.True(interrupted.Token.IsCancellationRequested);
        fixture.Back();
        var resumed = fixture.Coordinator.History[2];
        Assert.Equal(interrupted.Max, resumed.Max);
        Assert.Equal(interrupted.ViewAt, resumed.ViewAt);
        Assert.Same(first, Assert.Single(page.Medias));

        fixture.CompleteHistory(1, 999, "stale");
        await DrainAsync().ConfigureAwait(true);
        Assert.True(page.LoadingVisibility);
        Assert.Same(first, Assert.Single(page.Medias));
        fixture.CompleteHistory(2, 20, "resumed");
        await DrainAsync().ConfigureAwait(true);
        Assert.Collection(page.Medias,
            row => Assert.Equal("committed", row.Title),
            row => Assert.Equal("resumed", row.Title));
        page.LoadMoreCommand.Execute(null);
        Assert.Equal(20, fixture.Coordinator.History[3].Max);
        fixture.CompleteHistory(3, 30);
        await DrainAsync().ConfigureAwait(true);
        fixture.Away();
        fixture.Back();
        page.LoadMoreCommand.Execute(null);
        Assert.Equal(4, fixture.Coordinator.History.Count);
    }

    [AvaloniaFact]
    public async Task CanceledInitialReadsRestartAndLateFailuresCannotHideCurrentLoading()
    {
        foreach (var route in new[] { AppRoute.MyHistory, AppRoute.MyToViewVideo })
        {
            using var fixture = new Fixture();
            fixture.Enter(route);
            var oldToken = fixture.Token(route, 0);
            fixture.Away();
            fixture.Back();
            Assert.True(oldToken.IsCancellationRequested);
            Assert.Equal(2, fixture.RequestCount(route));
            fixture.Fail(route, 0);
            await DrainAsync().ConfigureAwait(true);
            Assert.True(fixture.IsLoading(route));
            Assert.False(fixture.IsEmpty(route));
            fixture.Complete(route, 1, "current");
            await DrainAsync().ConfigureAwait(true);
            Assert.Equal("current", fixture.Titles(route).Single());
            Assert.False(fixture.IsLoading(route));
        }
    }

    [AvaloniaFact]
    public async Task SuccessfulEmptyListsAreRetainedButFailedInitialReadsRetryOnReturn()
    {
        foreach (var route in new[] { AppRoute.MyHistory, AppRoute.MyToViewVideo })
        {
            using var fixture = new Fixture();
            fixture.Enter(route);
            fixture.Fail(route, 0);
            await DrainAsync().ConfigureAwait(true);
            Assert.True(fixture.IsEmpty(route));
            fixture.Away();
            fixture.Back();
            Assert.Equal(2, fixture.RequestCount(route));
            fixture.Complete(route, 1);
            await DrainAsync().ConfigureAwait(true);
            Assert.True(fixture.IsEmpty(route));
            fixture.Away();
            fixture.Back();
            Assert.Equal(2, fixture.RequestCount(route));
            Assert.True(fixture.IsEmpty(route));
        }
    }

    [AvaloniaFact]
    public async Task AccountSwitchAndLogoutInvalidateRetainedPersonalData()
    {
        foreach (var route in new[] { AppRoute.MyHistory, AppRoute.MyToViewVideo })
        {
            using var fixture = new Fixture();
            fixture.Enter(route);
            fixture.Complete(route, 0, "old account");
            await DrainAsync().ConfigureAwait(true);
            fixture.Away();
            fixture.SetAccount(43);
            fixture.Back();
            Assert.Empty(fixture.Titles(route));
            Assert.Equal(2, fixture.RequestCount(route));
            if (route == AppRoute.MyHistory) Assert.Equal(0, fixture.Coordinator.History[1].Max);
            fixture.Complete(route, 1, "new account");
            await DrainAsync().ConfigureAwait(true);
            Assert.Equal("new account", fixture.Titles(route).Single());
            fixture.Away();
            fixture.SetAccount(0);
            fixture.Back();
            Assert.Empty(fixture.Titles(route));
            Assert.Equal(2, fixture.RequestCount(route));
        }
    }

    private static async Task DrainAsync() =>
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"downkyi-personal-return-{Guid.NewGuid():N}");
        private readonly SettingsStore _settings;
        public ControlledCoordinator Coordinator { get; } = new();
        public AvaloniaNavigationService Navigation { get; }
        public MyHistoryViewModel History => (MyHistoryViewModel)Navigation.GetActiveView(AppNavigationRegion.Main)!;
        public MyToViewVideoViewModel ToView => (MyToViewVideoViewModel)Navigation.GetActiveView(AppNavigationRegion.Main)!;

        public Fixture()
        {
            DesktopTestResources.EnsureProductThemeResources();
            _settings = new SettingsStore(Path.Combine(_directory, "settings.json"));
            SetAccount(42);
            DesktopInteractionContext? interactions = null;
            Navigation = new AvaloniaNavigationService(route => route switch
            {
                AppRoute.MyHistory => new MyHistoryViewModel(interactions!, new UnusedDownloadCoordinator(), Coordinator,
                    _settings, NullLogger<MyHistoryViewModel>.Instance),
                AppRoute.MyToViewVideo => new MyToViewVideoViewModel(interactions!, new UnusedDownloadCoordinator(), Coordinator,
                    _settings, NullLogger<MyToViewVideoViewModel>.Instance),
                _ => new object()
            }, action => action());
            interactions = new DesktopInteractionContext(new Notifications(), Navigation, new UnusedDialogs());
        }

        public void SetAccount(long mid) => _settings.Update(settings => settings with
        {
            User = settings.User with { Mid = mid, IsLogin = mid > 0 }
        });
        public void Enter(AppRoute route) => Navigation.Navigate(new AppNavigationRequest(route, AppRoute.MySpace, 42L));
        public void Away() => Navigation.Navigate(new AppNavigationRequest(AppRoute.Toolbox));
        public void Back() => Navigation.GoBack(AppNavigationRegion.Main);
        public int RequestCount(AppRoute route) => route == AppRoute.MyHistory ? Coordinator.History.Count : Coordinator.ToView.Count;
        public CancellationToken Token(AppRoute route, int index) => route == AppRoute.MyHistory
            ? Coordinator.History[index].Token : Coordinator.ToView[index].Token;
        public bool IsLoading(AppRoute route) => route == AppRoute.MyHistory ? History.LoadingVisibility : ToView.LoadingVisibility;
        public bool IsEmpty(AppRoute route) => route == AppRoute.MyHistory ? History.NoDataVisibility : ToView.NoDataVisibility;
        public IEnumerable<string> Titles(AppRoute route) => route == AppRoute.MyHistory
            ? History.Medias.Select(row => row.Title) : ToView.Medias.Select(row => row.Title);
        public void Complete(AppRoute route, int index, params string[] titles)
        {
            if (route == AppRoute.MyHistory) CompleteHistory(index, 10, titles);
            else CompleteToView(index, titles);
        }
        public void Fail(AppRoute route, int index)
        {
            var exception = new HttpRequestException("Controlled read failure.");
            if (route == AppRoute.MyHistory) Coordinator.History[index].Response.SetException(exception);
            else Coordinator.ToView[index].Response.SetException(exception);
        }
        public void CompleteHistory(int index, long max, params string[] titles) => Coordinator.History[index].Response.SetResult(
            new HistoryPageSnapshot(titles.Select(title => new HistoryMedia(Navigation, AppRoute.MyHistory, _settings)
            {
                Title = title,
                Business = "archive",
                Bvid = "BV17x411w7KC",
                Url = "https://www.bilibili.com/video/BV17x411w7KC"
            }).ToArray(), max, max + 100, titles.Length > 0));
        public void CompleteToView(int index, params string[] titles) => Coordinator.ToView[index].Response.SetResult(
            titles.Select(title => new ToViewMedia(Navigation, AppRoute.MyToViewVideo, _settings)
            {
                Title = title,
                Bvid = "BV17x411w7KC"
            }).ToArray());

        public void Dispose()
        {
            Navigation.Dispose();
            foreach (var request in Coordinator.History) request.Response.TrySetCanceled(request.Token);
            foreach (var request in Coordinator.ToView) request.Response.TrySetCanceled(request.Token);
            _settings.Dispose();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class ControlledCoordinator : IPersonalMediaCoordinator
    {
        public List<HistoryRequest> History { get; } = [];
        public List<ToViewRequest> ToView { get; } = [];
        public Task<HistoryPageSnapshot> LoadHistoryPageAsync(long max, long viewAt, int pageSize, CancellationToken cancellationToken)
        {
            var request = new HistoryRequest(max, viewAt, new(TaskCreationOptions.RunContinuationsAsynchronously), cancellationToken);
            History.Add(request);
            return request.Response.Task;
        }
        public Task<IReadOnlyList<ToViewMedia>> LoadToViewAsync(CancellationToken cancellationToken)
        {
            var request = new ToViewRequest(new(TaskCreationOptions.RunContinuationsAsynchronously), cancellationToken);
            ToView.Add(request);
            return request.Response.Task;
        }
    }

    private sealed record HistoryRequest(long Max, long ViewAt, TaskCompletionSource<HistoryPageSnapshot> Response, CancellationToken Token);
    private sealed record ToViewRequest(TaskCompletionSource<IReadOnlyList<ToViewMedia>> Response, CancellationToken Token);
    private sealed class UnusedDownloadCoordinator : IContentDownloadCoordinator
    {
        public Task<ContentDownloadBatchResult?> AddAsync(IReadOnlyList<ContentDownloadItem> items, bool onlySelected,
            CancellationToken cancellationToken, IProgress<ContentDownloadBatchProgress>? progress = null) => throw new NotSupportedException();
    }
    private sealed class Notifications : IUserNotificationService
    {
        public event EventHandler<UserNotificationEventArgs>? NotificationRaised { add { } remove { } }
        public void Show(string message) { }
    }
    private sealed class UnusedDialogs : IAppDialogService
    {
        public Task<AppDialogResult> ShowAsync(AppDialogRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
