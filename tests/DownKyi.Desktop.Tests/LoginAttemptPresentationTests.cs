using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DownKyi.Application.Desktop;
using DownKyi.Core.BiliApi.Login.Models;
using DownKyi.Infrastructure.Storage;
using DownKyi.Services.Account;
using DownKyi.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Desktop.Tests;

public sealed class LoginAttemptPresentationTests
{
    [AvaloniaFact]
    public void LeavingBeforeQueuedQrPresentationPreventsRenderingTheOldAttempt()
    {
        using var coordinator = new LoginCoordinatorStub();
        var renderer = new CountingRenderer();
        using var viewModel = new LoginViewModel(new InteractionStub(), coordinator, renderer,
            NullLogger<LoginViewModel>.Instance);
        var context = new AppNavigationContext(AppNavigationRegion.Main, AppRoute.Login,
            AppRoute.Index, null, new AppNavigationParameters());

        viewModel.OnNavigatedTo(context);
        viewModel.OnNavigatedFrom(context);
        Dispatcher.UIThread.RunJobs();

        Assert.True(coordinator.Token.IsCancellationRequested);
        Assert.Equal(0, renderer.RenderCount);
        Assert.Null(viewModel.LoginQrCode);
    }

    private sealed class CountingRenderer : ILoginQrCodeRenderer
    {
        public int RenderCount { get; private set; }
        public Bitmap Render(Uri loginUri)
        {
            RenderCount++;
            return new LoginQrCodeRenderer().Render(loginUri);
        }
    }

    private sealed class InteractionStub : IDesktopInteractionContext
    {
        public IUserNotificationService Notifications => null!;
        public IAppNavigationService Navigation => null!;
        public IAppDialogService Dialogs => null!;
    }

    private sealed class LoginCoordinatorStub : ILoginCoordinator
    {
        public CancellationToken Token { get; private set; }
        public Task<LoginUrlOrigin?> RequestLoginUrlAsync(CancellationToken cancellationToken)
        {
            Token = cancellationToken;
            return Task.FromResult<LoginUrlOrigin?>(new LoginUrlOrigin
            {
                Data = new LoginUrl { QrCodeAddress = "https://example.invalid/fixture", QrcodeKey = "fixture" }
            });
        }
        public Task<LoginStatusResult?> GetLoginStatusAsync(string qrcodeKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> SaveLoginCookiesAsync(LoginStatusResult loginStatus, Uri redirectUri, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> CommitLoginCookiesAsync(IReadOnlyList<DownKyiCookie> cookies, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
