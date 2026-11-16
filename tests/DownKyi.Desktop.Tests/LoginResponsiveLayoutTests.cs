using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using DownKyi.Application.Desktop;
using DownKyi.Core.BiliApi.Login.Models;
using DownKyi.Infrastructure.Storage;
using DownKyi.Services.Account;
using DownKyi.ViewModels;
using DownKyi.Views;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Desktop.Tests;

public sealed class LoginResponsiveLayoutTests
{
    [AvaloniaFact]
    public void ResizingPreservesMaskedInputAndKeepsQrCodeReadableAcrossThemes()
    {
        var application = DesktopTestResources.EnsureProductThemeResources();
        var originalTheme = application.RequestedThemeVariant;
        var fluent = new FluentTheme();
        application.Styles.Insert(0, fluent);
        using var coordinator = new LoginCoordinatorStub();
        using var viewModel = new LoginViewModel(
            new InteractionStub(), coordinator, new LoginQrCodeRenderer(), NullLogger<LoginViewModel>.Instance)
        {
            LoginQrCode = new LoginQrCodeRenderer().Render(new Uri("https://example.invalid/qr-layout-test")),
            LoginQrCodeOpacity = 1
        };
        var view = new ViewLogin { DataContext = viewModel };
        var window = new Window { Content = view, Width = 800, Height = 550 };
        try
        {
            window.Show();
            var input = view.FindControl<TextBox>("BrowserCookieInput")!;
            var login = view.FindControl<Button>("BrowserCookieLoginButton")!;
            var qr = view.FindControl<Image>("NameLoginQrCode")!;
            var paper = view.FindControl<Border>("LoginQrPaper")!;
            var scan = view.FindControl<Border>("NameLoginQrCodeStatus")!;
            input.Text = "synthetic-input-preserved-on-resize";
            Assert.True(input.Focus());
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                application.RequestedThemeVariant = theme;
                foreach (var size in new[] { new Size(798, 518), new Size(960, 1000), new Size(999, 760), new Size(1000, 760), new Size(1440, 900), new Size(1920, 1080), new Size(800, 550) })
                {
                    window.Width = size.Width;
                    window.Height = size.Height;
                    window.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    foreach (var control in new Control[] { paper, input, login })
                    {
                        var position = control.TranslatePoint(default, view)!.Value;
                        Assert.InRange(position.X, 0, view.Bounds.Width - control.Bounds.Width + 0.5);
                        Assert.InRange(position.Y, 0, view.Bounds.Height - control.Bounds.Height + 0.5);
                    }
                    Assert.Equal(new Size(200, 200), qr.Bounds.Size);
                    Assert.Equal(Colors.White, Assert.IsAssignableFrom<ISolidColorBrush>(paper.Background).Color);
                    Assert.True(paper.Padding.Left >= 16);
                    Assert.True(input.IsFocused);
                    Assert.Equal('●', input.PasswordChar);
                    Assert.Equal("synthetic-input-preserved-on-resize", viewModel.BrowserCookieHeader);
                    Assert.Same(viewModel.BrowserCookieLoginCommand, login.Command);
                    Assert.True(login.IsEffectivelyEnabled);
                    Assert.False(scan.IsVisible);
                }
            }
            viewModel.LoginQrCodeStatus = true;
            viewModel.LoginQrCodeOpacity = 0.3;
            window.UpdateLayout();
            Assert.True(scan.IsVisible);
            Assert.True(view.FindControl<TextBlock>("LoginScannedMessage")!.IsVisible);
            Assert.Equal(0.3, qr.Opacity);
        }
        finally
        {
            window.Close();
            application.RequestedThemeVariant = originalTheme;
            application.Styles.Remove(fluent);
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
        public Task<LoginUrlOrigin?> RequestLoginUrlAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<LoginStatusResult?> GetLoginStatusAsync(string qrcodeKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> SaveLoginCookiesAsync(LoginStatusResult loginStatus, Uri redirectUri, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> CommitLoginCookiesAsync(IReadOnlyList<DownKyiCookie> cookies, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
