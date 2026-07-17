using System;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Core.BiliApi.Login;
using DownKyi.Core.BiliApi.Login.Models;
using DownKyi.Core.Logging;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Account;

internal interface ILoginCoordinator
{
    Task<LoginUrlOrigin?> RequestLoginUrlAsync(CancellationToken cancellationToken);

    Task<LoginStatus?> GetLoginStatusAsync(string qrcodeKey, CancellationToken cancellationToken);

    Task<bool> SaveLoginCookiesAsync(Uri redirectUri, CancellationToken cancellationToken);
}

internal sealed class LoginCoordinator : ILoginCoordinator
{
    private readonly ILogger<LoginCoordinator> _logger;

    public LoginCoordinator(ILogger<LoginCoordinator> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<LoginUrlOrigin?> RequestLoginUrlAsync(CancellationToken cancellationToken)
    {
        return RunAsync(() =>
        {
            var result = LoginQr.GetLoginUrl();
            if (result == null)
            {
                _logger.LogWarningMessage("Bilibili login URL could not be created.");
            }

            return result;
        }, cancellationToken);
    }

    public Task<LoginStatus?> GetLoginStatusAsync(
        string qrcodeKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(qrcodeKey);
        return RunAsync(() =>
        {
            var result = LoginQr.GetLoginStatus(qrcodeKey);
            if (result == null)
            {
                _logger.LogWarningMessage("Bilibili login status could not be read.");
            }

            return result;
        }, cancellationToken);
    }

    public Task<bool> SaveLoginCookiesAsync(Uri redirectUri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(redirectUri);
        return RunAsync(() =>
        {
            var saved = LoginHelper.SaveLoginInfoCookies(redirectUri);
            if (!saved)
            {
                _logger.LogWarningMessage("Bilibili login cookies could not be persisted.");
            }

            return saved;
        }, cancellationToken);
    }

    private static Task<T> RunAsync<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = operation();
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }, cancellationToken);
    }
}
