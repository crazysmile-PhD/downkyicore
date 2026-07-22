using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Core.BiliApi;
using DownKyi.Core.BiliApi.Login;
using DownKyi.Core.Logging;
using DownKyi.Services.Account;
using DownKyi.Utils;
using Microsoft.Extensions.Logging;

namespace DownKyi.ViewModels;

internal class ViewLoginViewModel : ViewModelBase
{
    public const string Tag = "PageLogin";

    private readonly ILoginCoordinator _loginCoordinator;
    private readonly ILogger<ViewLoginViewModel> _logger;
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

    #endregion

    public ViewLoginViewModel(
        IDesktopInteractionContext desktopInteractions,
        ILoginCoordinator loginCoordinator,
        ILogger<ViewLoginViewModel> logger) : base(desktopInteractions)
    {
        _loginCoordinator = loginCoordinator ?? throw new ArgumentNullException(nameof(loginCoordinator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private RelayCommand? _backSpaceCommand;

    public RelayCommand BackSpaceCommand => _backSpaceCommand ??= new RelayCommand(ExecuteBackSpace);

    protected internal override void ExecuteBackSpace()
    {
        // 初始化状态
        InitStatus();

        // 结束任务
        _tokenSource?.Cancel();
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

            PropertyChangeAsync(() => { LoginQrCode = LoginQr.GetLoginQrCode(loginUri); });

            await GetLoginStatusAsync(loginUrl.Data.QrcodeKey, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or InvalidOperationException or ArgumentException
            or FormatException or Newtonsoft.Json.JsonException)
        {
            _logger.LogErrorMessage("Login initialization failed.", e);
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
            if (loginStatus == null)
            {
                continue;
            }

            var loginData = loginStatus.Data ?? throw new BilibiliApiResponseException(
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
                        LoginQrCodeStatus = true;
                        LoginQrCodeOpacity = 0.3;
                    });
                    break;
                case 0:
                    // 确认登录

                    // 发送通知
                    Notifications.Show(DictionaryResource.GetString("LoginSuccessful"));
                    _logger.LogInformationMessage("Login completed successfully.");

                    // 保存登录信息
                    try
                    {
                        var redirectUri = new Uri(loginData.RedirectAddress, UriKind.Absolute);
                        var isSucceed = await _loginCoordinator
                            .SaveLoginCookiesAsync(redirectUri, cancellationToken)
                            .ConfigureAwait(true);
                        if (!isSucceed)
                        {
                            Notifications.Show(DictionaryResource.GetString("LoginFailed"));
                            _logger.LogErrorMessage("Login cookies could not be persisted.");
                        }
                    }
                    catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException
                        or InvalidOperationException or ArgumentException or Newtonsoft.Json.JsonException)
                    {
                        _logger.LogErrorMessage("Login cookie persistence failed.", e);
                        Notifications.Show(DictionaryResource.GetString("LoginFailed"));
                    }

                    // 取消任务
                    await Task.Delay(3000, cancellationToken).ConfigureAwait(true);
                    PropertyChange(ExecuteBackSpace);
                    return;
            }

            // 判断是否该结束线程，若为true，跳出while循环
            if (!cancellationToken.IsCancellationRequested) continue;
            _logger.LogDebugMessage("Login polling stopped.");
            break;
        }
    }

    private async Task RestartLoginAsync()
    {
        var replacement = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _tokenSource, replacement);
        if (previous != null)
        {
            await previous.CancelAsync().ConfigureAwait(true);
            previous.Dispose();
        }

        RunFireAndForget(LoginAsync(replacement.Token), $"{Tag}.LoginAsync", _logger);
    }


    /// <summary>
    /// 初始化状态
    /// </summary>
    private void InitStatus()
    {
        LoginQrCode = null;
        LoginQrCodeOpacity = 1;
        LoginQrCodeStatus = false;
    }

    public override void OnNavigatedTo(AppNavigationContext navigationContext)
    {
        base.OnNavigatedTo(navigationContext);

        InitStatus();

        RunFireAndForget(RestartLoginAsync(), $"{Tag}.RestartLoginAsync", _logger);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            _tokenSource?.Cancel();
            _tokenSource?.Dispose();
            _tokenSource = null;
            LoginQrCode?.Dispose();
            LoginQrCode = null;
        }

        base.Dispose(disposing);
    }
}
