using Avalonia.Media.Imaging;
using DownKyi.Application.Bilibili;
using DownKyi.Application.Desktop;
using DownKyi.Core.BiliApi.Login.Models;
using DownKyi.Infrastructure.Storage;
using DownKyi.Platform;
using DownKyi.Services.Account;
using DownKyi.Utils;
using DownKyi.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class LoginViewModelTests
{
    [Fact]
    public async Task ForwardNavigationCancelsRetainedLoginAndReturningStartsOneNewAttempt()
    {
        var requests = new List<(CancellationToken Token, TaskCompletionSource Exited)>();
        using var coordinator = new StubLoginCoordinator
        {
            LoginUrlHandler = token =>
            {
                var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                requests.Add((token, exited));
                return HoldRequestAsync(exited, token);
            }
        };
        LoginViewModel? login = null;
        using var navigation = new AvaloniaNavigationService(
            route => route == AppRoute.Login ? login! : new object(), action => action());
        using var viewModel = new LoginViewModel(new TestDesktopInteractionContext(navigation), coordinator,
            new StubLoginQrCodeRenderer(), NullLogger<LoginViewModel>.Instance);
        login = viewModel;

        navigation.Navigate(new AppNavigationRequest(AppRoute.Login));
        var first = Assert.Single(requests);
        navigation.Navigate(new AppNavigationRequest(AppRoute.Toolbox, AppRoute.Login));

        Assert.True(navigation.CanGoBack(AppNavigationRegion.Main));
        Assert.True(first.Token.IsCancellationRequested);
        await first.Exited.Task.WaitAsync(TestContext.Current.CancellationToken);
        navigation.GoBack(AppNavigationRegion.Main);

        Assert.Equal(2, requests.Count);
        Assert.NotEqual(first.Token, requests[1].Token);
        Assert.False(requests[1].Token.IsCancellationRequested);
        navigation.Dispose();
        Assert.True(requests[1].Token.IsCancellationRequested);
        await requests[1].Exited.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LateCookieCommitAfterLeavingCannotNotifyOrNavigate(bool succeeded)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var interactions = new TestDesktopInteractionContext();
        var notifications = new List<string>();
        interactions.Notifications.NotificationRaised += (_, args) => notifications.Add(args.Message);
        var observedToken = CancellationToken.None;
        using var coordinator = new StubLoginCoordinator
        {
            CommitHandler = (_, token) =>
            {
                observedToken = token;
                return result.Task;
            }
        };
        using var login = new LoginViewModel(interactions, coordinator,
            new StubLoginQrCodeRenderer(), NullLogger<LoginViewModel>.Instance)
        {
            BrowserCookieHeader = "SESSDATA=fixture%2Fsession"
        };
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = login.BrowserCookieLoginCommand;
        command.CanExecuteChanged += (_, _) =>
        {
            if (command.CanExecute(null)) finished.TrySetResult();
        };

        command.Execute(null);
        login.OnNavigatedFrom(new AppNavigationContext(AppNavigationRegion.Main, AppRoute.Login,
            AppRoute.Index, null, new AppNavigationParameters()));
        Assert.True(observedToken.IsCancellationRequested);
        result.SetResult(succeeded);
        await finished.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Empty(notifications);
        Assert.Empty(((TestNavigationService)interactions.Navigation).Requests);
    }

    private static async Task<LoginUrlOrigin?> HoldRequestAsync(
        TaskCompletionSource exited,
        CancellationToken token)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return null;
        }
        finally
        {
            exited.TrySetResult();
        }
    }

    [Fact]
    public async Task LoginUrlTimeoutShowsFailureInsteadOfLeavingTheQrCodeSilentlyEmpty()
    {
        var notification = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var interactions = new TestDesktopInteractionContext();
        interactions.Notifications.NotificationRaised += (_, e) => notification.TrySetResult(e.Message);
        using var coordinator = new StubLoginCoordinator
        {
            LoginUrlHandler = _ => Task.FromException<LoginUrlOrigin?>(new BilibiliHttpRequestException(
                "Fixture login timeout.", BilibiliHttpFailureKind.Timeout))
        };
        using var viewModel = new LoginViewModel(
            interactions,
            coordinator,
            new StubLoginQrCodeRenderer(),
            NullLogger<LoginViewModel>.Instance);

        viewModel.OnNavigatedTo(new AppNavigationContext(
            AppNavigationRegion.Main, AppRoute.Login, AppRoute.Index, null, new AppNavigationParameters()));

        Assert.Equal(DictionaryResource.GetString("GetLoginUrlFailed"),
            await notification.Task.WaitAsync(TestContext.Current.CancellationToken));
        Assert.Null(viewModel.LoginQrCode);
    }

    [Fact]
    public async Task TransportCancellationShowsLoginFailureWhenLoginTokenIsActive()
    {
        var notifications = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var interactions = new TestDesktopInteractionContext();
        interactions.Notifications.NotificationRaised += (_, e) =>
            notifications.TrySetResult(e.Message);
        var observedCancellationToken = CancellationToken.None;
        using var coordinator = new StubLoginCoordinator
        {
            CommitHandler = (_, cancellationToken) =>
            {
                observedCancellationToken = cancellationToken;
                return Task.FromException<bool>(
                    new OperationCanceledException("Fixture transport timeout."));
            }
        };
        using var viewModel = new LoginViewModel(
            interactions,
            coordinator,
            new StubLoginQrCodeRenderer(),
            NullLogger<LoginViewModel>.Instance)
        {
            BrowserCookieHeader = "SESSDATA=fixture%2Fsession"
        };
        var commandEnabled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var command = viewModel.BrowserCookieLoginCommand;
        command.CanExecuteChanged += (_, _) =>
        {
            if (command.CanExecute(null))
            {
                commandEnabled.TrySetResult();
            }
        };

        command.Execute(null);

        var message = await notifications.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DictionaryResource.GetString("LoginFailed"), message);
        Assert.True(observedCancellationToken.CanBeCanceled);
        Assert.False(observedCancellationToken.IsCancellationRequested);
        Assert.Equal(string.Empty, viewModel.BrowserCookieHeader);

        viewModel.ExecuteBackSpace();
        await commandEnabled.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(command.CanExecute(null));
    }

    private sealed class StubLoginCoordinator : ILoginCoordinator
    {
        public Func<CancellationToken, Task<LoginUrlOrigin?>>? LoginUrlHandler { get; init; }

        public Func<IReadOnlyList<DownKyiCookie>, CancellationToken, Task<bool>>? CommitHandler
        {
            get;
            init;
        }

        public Task<LoginUrlOrigin?> RequestLoginUrlAsync(CancellationToken cancellationToken)
        {
            return LoginUrlHandler?.Invoke(cancellationToken) ?? throw new NotSupportedException();
        }

        public Task<LoginStatusResult?> GetLoginStatusAsync(
            string qrcodeKey,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<bool> SaveLoginCookiesAsync(
            LoginStatusResult loginStatus,
            Uri redirectUri,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<bool> CommitLoginCookiesAsync(
            IReadOnlyList<DownKyiCookie> cookies,
            CancellationToken cancellationToken)
        {
            return CommitHandler?.Invoke(cookies, cancellationToken)
                   ?? throw new InvalidOperationException("No commit handler was configured.");
        }

        public void Dispose()
        {
        }
    }

    private sealed class StubLoginQrCodeRenderer : ILoginQrCodeRenderer
    {
        public Bitmap Render(Uri loginUri)
        {
            throw new NotSupportedException();
        }
    }
}
