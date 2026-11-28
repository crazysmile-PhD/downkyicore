using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Application.Diagnostics;
using DownKyi.Core.BiliApi;
using DownKyi.Infrastructure.Bilibili;
using DownKyi.Services.Account;
using DownKyi.Utils;
using Microsoft.Extensions.Logging;

namespace DownKyi.ViewModels;

internal class LoginViewModel : ViewModelBase
{
    public const string Tag = "PageLogin";

    private readonly ILoginCoordinator _loginCoordinator;
    private readonly ILoginQrCodeRenderer _loginQrCodeRenderer;
    private readonly ILogger<LoginViewModel> _logger;
    private CancellationTokenSource? _tokenSource;

    #region 页面属性申明

    private Bitmap? _loginQrCode;

    public Bitmap? LoginQrCode
    {
        get => _loginQrCode;
        set => SetProperty(ref _loginQrCode, value);
    }

    private double _loginQrCodeOpacity;

    public double LoginQrCodeOpacity
    {
        get => _loginQrCodeOpacity;
        set => SetProperty(ref _loginQrCodeOpacity, value);
    }

    private bool _loginQrCodeStatus;

    public bool LoginQrCodeStatus
    {
        get => _loginQrCodeStatus;
        set => SetProperty(ref _loginQrCodeStatus, value);
    }

    private string _browserCookieHeader = string.Empty;

    public string BrowserCookieHeader
    {
        get => _browserCookieHeader;
        set => SetProperty(ref _browserCookieHeader, value);
    }

    #endregion

    public LoginViewModel(
        IDesktopInteractionContext desktopInteractions,
        ILoginCoordinator loginCoordinator,
        ILoginQrCodeRenderer loginQrCodeRenderer,
        ILogger<LoginViewModel> logger) : base(desktopInteractions)
    {
        _loginCoordinator = loginCoordinator ?? throw new ArgumentNullException(nameof(loginCoordinator));
        _loginQrCodeRenderer = loginQrCodeRenderer
            ?? throw new ArgumentNullException(nameof(loginQrCodeRenderer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private RelayCommand? _backSpaceCommand;

    public RelayCommand BackSpaceCommand => _backSpaceCommand ??= new RelayCommand(ExecuteBackSpace);

    private RelayCommand? _browserCookieLoginCommand;
    private bool _browserCookieLoginRunning;

    public RelayCommand BrowserCookieLoginCommand =>
        _browserCookieLoginCommand ??= new RelayCommand(
            ExecuteBrowserCookieLogin,
            () => !_browserCookieLoginRunning);

    protected internal override void ExecuteBackSpace()
    {
        EndLoginAttempt();
        if (TryNavigateBack())
        {
            return;
        }

        NavigateToParent("login");
    }

    /// <summary>
    /// 登录
    /// </summary>
    private async Task LoginAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var loginUrl = await _loginCoordinator
                .RequestLoginUrlAsync(cancellationToken)
                .ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            if (loginUrl == null)
            {
                return;
            }

            if (loginUrl.Code != 0)
            {
                ExecuteBackSpace();
                return;
            }

            if (loginUrl.Data?.QrCodeAddress == null || loginUrl.Data?.QrcodeKey == null)
            {
                Notifications.Show(DictionaryResource.GetString("GetLoginUrlFailed"));
                return;
            }

            if (!Uri.TryCreate(loginUrl.Data.QrCodeAddress, UriKind.Absolute, out var loginUri))
            {
                Notifications.Show(DictionaryResource.GetString("GetLoginUrlFailed"));
                return;
            }

            PropertyChangeAsync(() =>
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    ReplaceQrCode(_loginQrCodeRenderer.Render(loginUri));
                }
            });

            await GetLoginStatusAsync(loginUrl.Data.QrcodeKey, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or InvalidOperationException or ArgumentException
            or FormatException or Newtonsoft.Json.JsonException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogErrorMessage("Login initialization failed.", e);
            Notifications.Show(DictionaryResource.GetString("GetLoginUrlFailed"));
        }
    }

    /// <summary>
    /// 循环查询登录状态
    /// </summary>
    /// <param name="oauthKey"></param>
    private async Task GetLoginStatusAsync(string oauthKey, CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(1000, cancellationToken).ConfigureAwait(true);
            var loginStatus = await _loginCoordinator
                .GetLoginStatusAsync(oauthKey, cancellationToken)
                .ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            if (loginStatus == null)
            {
                continue;
            }

            var loginData = loginStatus.Status.Data ?? throw new BilibiliApiResponseException(
                nameof(GetLoginStatusAsync),
                "Login status response did not contain its required data payload.");
            switch (loginData.Code)
            {
                case 86038:
                    // 二维码已失效
                    // 发送通知
                    Notifications.Show(DictionaryResource.GetString("LoginTimeOut"));
                    _logger.LogInformationMessage("Login QR code timed out.");

                    await RestartLoginAsync().ConfigureAwait(true);
                    return;
                case 86101:
                    // 未扫码
                    break;
                case 86090:
                    // 已扫码，未确认
                    PropertyChangeAsync(() =>
                    {
                        if (cancellationToken.IsCancellationRequested) return;
                        LoginQrCodeStatus = true;
                        LoginQrCodeOpacity = 0.3;
                    });
                    break;
                case 0:
                    // 确认登录
                    var redirectUri = new Uri(loginData.RedirectAddress, UriKind.Absolute);
                    await FinishLoginAttemptAsync(
                        () => _loginCoordinator.SaveLoginCookiesAsync(
                            loginStatus,
                            redirectUri,
                            cancellationToken),
                        cancellationToken).ConfigureAwait(true);
                    return;
            }
        }
    }

    private void ExecuteBrowserCookieLogin()
    {
        if (_browserCookieLoginRunning)
        {
            return;
        }

        _browserCookieLoginRunning = true;
        _browserCookieLoginCommand?.NotifyCanExecuteChanged();
        RunFireAndForget(
            LoginWithBrowserCookiesAsync(),
            $"{Tag}.LoginWithBrowserCookiesAsync",
            _logger);
    }

    private async Task LoginWithBrowserCookiesAsync()
    {
        var cancellationToken = CancellationToken.None;
        try
        {
            var cookies = BilibiliCookieProvider.ParseBrowserCookieHeader(BrowserCookieHeader);
            if (cookies.Count == 0)
            {
                Notifications.Show(DictionaryResource.GetString("BrowserCookieInvalid"));
                return;
            }

            BrowserCookieHeader = string.Empty;
            cancellationToken = await ReplaceLoginCancellationAsync().ConfigureAwait(true);
            await FinishLoginAttemptAsync(
                () => _loginCoordinator.CommitLoginCookiesAsync(cookies, cancellationToken),
                cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            _browserCookieLoginRunning = false;
            _browserCookieLoginCommand?.NotifyCanExecuteChanged();
        }
    }

    private async Task FinishLoginAttemptAsync(
        Func<Task<bool>> commit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var isSucceed = false;
        try
        {
            isSucceed = await commit().ConfigureAwait(true);
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogErrorMessage("Login cookie validation was canceled by the transport.", e);
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException
            or System.Net.Http.HttpRequestException or InvalidOperationException
            or ArgumentException or FormatException or Newtonsoft.Json.JsonException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogErrorMessage("Login cookie persistence failed.", e);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (isSucceed)
        {
            Notifications.Show(DictionaryResource.GetString("LoginSuccessful"));
            _logger.LogInformationMessage("Login completed successfully.");
        }
        else
        {
            Notifications.Show(DictionaryResource.GetString("LoginFailed"));
            _logger.LogErrorMessage("Login cookies could not be persisted and validated.");
        }

        await Task.Delay(3000, cancellationToken).ConfigureAwait(true);
        PropertyChange(() =>
        {
            if (!cancellationToken.IsCancellationRequested) ExecuteBackSpace();
        });
    }

    private async Task<CancellationToken> ReplaceLoginCancellationAsync()
    {
        var replacement = new CancellationTokenSource();
        var cancellationToken = replacement.Token;
        var previous = Interlocked.Exchange(ref _tokenSource, replacement);
        if (previous != null)
        {
            await previous.CancelAsync().ConfigureAwait(true);
            previous.Dispose();
        }

        return cancellationToken;
    }

    private async Task RestartLoginAsync()
    {
        var cancellationToken = await ReplaceLoginCancellationAsync().ConfigureAwait(true);
        RunFireAndForget(LoginAsync(cancellationToken), $"{Tag}.LoginAsync", _logger);
    }


    /// <summary>
    /// 初始化状态
    /// </summary>
    private void InitStatus()
    {
        ReplaceQrCode(null);
        LoginQrCodeOpacity = 1;
        LoginQrCodeStatus = false;
        BrowserCookieHeader = string.Empty;
    }

    private void ReplaceQrCode(Bitmap? replacement)
    {
        var preceding = LoginQrCode;
        LoginQrCode = replacement;
        preceding?.Dispose();
    }

    private void EndLoginAttempt()
    {
        CancelAndDispose(ref _tokenSource);
        InitStatus();
    }

    public override void OnNavigatedTo(AppNavigationContext navigationContext)
    {
        base.OnNavigatedTo(navigationContext);

        InitStatus();

        RunFireAndForget(RestartLoginAsync(), $"{Tag}.RestartLoginAsync", _logger);
    }

    public override void OnNavigatedFrom(AppNavigationContext navigationContext)
    {
        EndLoginAttempt();
        base.OnNavigatedFrom(navigationContext);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            EndLoginAttempt();
        }

        base.Dispose(disposing);
    }
}
