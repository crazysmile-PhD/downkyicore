using Avalonia.Threading;
using DownKyi.Application.Desktop;
using DownKyi.Platform;
using DownKyi.Services.Toolbox;
using DownKyi.ViewModels;
using DownKyi.ViewModels.Toolbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Desktop.Tests;

public sealed class ToolboxLookupLifetimeTests
{
    [AvaloniaFact]
    public async Task LeavingParentCancelsLookupAndReturningStartsWithANewManualAttempt()
    {
        using var fixture = new Fixture();
        fixture.Navigation.Navigate(new AppNavigationRequest(AppRoute.Toolbox));
        await DrainAsync().ConfigureAwait(true);
        var oldHelper = fixture.Helper;
        oldHelper.FindDanmakuSenderCommand.Execute(null);
        var oldRequest = Assert.Single(fixture.Coordinator.Requests);

        fixture.Navigation.Navigate(new AppNavigationRequest(AppRoute.Index));
        Assert.Same(oldHelper, fixture.Helper);
        Assert.True(oldRequest.Token.IsCancellationRequested);
        oldRequest.Response.SetResult("late");
        await DrainAsync().ConfigureAwait(true);
        Assert.Null(oldHelper.UserMid);

        fixture.Navigation.GoBack(AppNavigationRegion.Main);
        await DrainAsync().ConfigureAwait(true);
        var current = fixture.Helper;
        Assert.NotSame(oldHelper, current);
        Assert.Single(fixture.Coordinator.Requests);
        current.FindDanmakuSenderCommand.Execute(null);
        var newRequest = fixture.Coordinator.Requests[1];
        Assert.False(newRequest.Token.IsCancellationRequested);
        newRequest.Response.SetResult("current");
        await DrainAsync().ConfigureAwait(true);
        Assert.Equal("current", current.UserMid);
    }

    [AvaloniaFact]
    public async Task ReplacingChildCancelsLookupAndLateFailureDoesNotClearItsResult()
    {
        using var fixture = new Fixture();
        fixture.Navigation.Navigate(new AppNavigationRequest(AppRoute.Toolbox));
        await DrainAsync().ConfigureAwait(true);
        var helper = fixture.Helper;
        helper.UserMid = "previous";
        helper.FindDanmakuSenderCommand.Execute(null);
        var request = Assert.Single(fixture.Coordinator.Requests);

        fixture.Parent.LeftTabHeadersCommand.Execute(fixture.Parent.TabHeaders[1]);
        Assert.True(request.Token.IsCancellationRequested);
        request.Response.SetException(new FormatException("fixture"));
        await DrainAsync().ConfigureAwait(true);
        Assert.Equal("previous", helper.UserMid);
        Assert.IsNotType<BiliHelperViewModel>(fixture.Navigation.GetActiveView(AppNavigationRegion.Toolbox));
    }

    [AvaloniaFact]
    public async Task QueuedInitialNavigationCannotOpenAChildAfterParentLeaves()
    {
        using var fixture = new Fixture();
        fixture.Navigation.Navigate(new AppNavigationRequest(AppRoute.Toolbox));
        fixture.Navigation.Navigate(new AppNavigationRequest(AppRoute.Index));
        await DrainAsync().ConfigureAwait(true);
        Assert.Null(fixture.Navigation.GetActiveView(AppNavigationRegion.Toolbox));
    }

    [AvaloniaFact]
    public async Task DisposingNavigationCancelsItsActiveLookup()
    {
        using var fixture = new Fixture();
        fixture.Navigation.Navigate(new AppNavigationRequest(AppRoute.Toolbox));
        await DrainAsync().ConfigureAwait(true);
        fixture.Helper.FindDanmakuSenderCommand.Execute(null);
        var request = Assert.Single(fixture.Coordinator.Requests);
        fixture.Navigation.Dispose();
        Assert.True(request.Token.IsCancellationRequested);
    }

    private static async Task DrainAsync() =>
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);

    private sealed class Fixture : IDisposable
    {
        public ControlledCoordinator Coordinator { get; } = new();
        public AvaloniaNavigationService Navigation { get; }
        public ToolboxViewModel Parent => (ToolboxViewModel)Navigation.GetActiveView(AppNavigationRegion.Main)!;
        public BiliHelperViewModel Helper => (BiliHelperViewModel)Navigation.GetActiveView(AppNavigationRegion.Toolbox)!;

        public Fixture()
        {
            DesktopTestResources.EnsureProductThemeResources();
            InteractionStub? interactions = null;
            Navigation = new AvaloniaNavigationService(route => route switch
            {
                AppRoute.Toolbox => new ToolboxViewModel(interactions!),
                AppRoute.BiliHelper => new BiliHelperViewModel(interactions!, Coordinator,
                    new Launcher(), NullLogger<BiliHelperViewModel>.Instance),
                _ => new object()
            }, action => action());
            interactions = new InteractionStub(Navigation);
        }

        public void Dispose()
        {
            Navigation.Dispose();
            foreach (var request in Coordinator.Requests) request.Response.TrySetResult(null);
        }
    }

    private sealed class ControlledCoordinator : IBiliHelperCoordinator
    {
        public List<LookupRequest> Requests { get; } = [];
        public string? ConvertAvidToBvid(string? input) => null;
        public string? ConvertBvidToAvid(string? input) => null;
        public Task<string?> FindDanmakuSenderAsync(string? userId, CancellationToken cancellationToken)
        {
            var response = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Requests.Add(new LookupRequest(response, cancellationToken));
            return response.Task;
        }
    }

    private sealed record LookupRequest(TaskCompletionSource<string?> Response, CancellationToken Token);
    private sealed class InteractionStub(IAppNavigationService navigation) : IDesktopInteractionContext
    {
        public IAppNavigationService Navigation => navigation;
        public IUserNotificationService Notifications { get; } = new NotificationsStub();
        public IAppDialogService Dialogs => null!;
    }
    private sealed class NotificationsStub : IUserNotificationService
    {
        public event EventHandler<UserNotificationEventArgs>? NotificationRaised { add { } remove { } }
        public void Show(string message) { }
    }
    private sealed class Launcher : IPlatformLauncher
    {
        public Task<bool> OpenFileAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> OpenFolderAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> OpenUriAsync(Uri uri, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
