using Avalonia.Media.Imaging;
using DownKyi.Core.BiliApi.Login.Models;
using DownKyi.Core.Storage;
using DownKyi.Services.Account;
using DownKyi.Utils;
using DownKyi.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class ViewLoginViewModelTests
{
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
        using var viewModel = new ViewLoginViewModel(
            interactions,
            coordinator,
            new StubLoginQrCodeRenderer(),
            NullLogger<ViewLoginViewModel>.Instance)
        {
            BrowserCookieHeader = "SESSDATA=fixture%2Fsession"
        };

        viewModel.BrowserCookieLoginCommand.Execute(null);

        var message = await notifications.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.Equal(DictionaryResource.GetString("LoginFailed"), message);
        Assert.True(observedCancellationToken.CanBeCanceled);
        Assert.False(observedCancellationToken.IsCancellationRequested);
        Assert.Equal(string.Empty, viewModel.BrowserCookieHeader);

        viewModel.ExecuteBackSpace();
        for (var attempt = 0;
             attempt < 50 && !viewModel.BrowserCookieLoginCommand.CanExecute(null);
             attempt++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.True(viewModel.BrowserCookieLoginCommand.CanExecute(null));
    }

    private sealed class StubLoginCoordinator : ILoginCoordinator
    {
        public Func<IReadOnlyList<DownKyiCookie>, CancellationToken, Task<bool>>? CommitHandler
        {
            get;
            init;
        }

        public Task<LoginUrlOrigin?> RequestLoginUrlAsync(CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
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
