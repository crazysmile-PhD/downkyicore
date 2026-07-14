using System;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Core.BiliApi.Login;
using DownKyi.Core.BiliApi.Login.Models;

namespace DownKyi.Services.Account;

internal interface ILoginCoordinator
{
    Task<LoginUrlOrigin?> RequestLoginUrlAsync(CancellationToken cancellationToken);

    Task<LoginStatus?> GetLoginStatusAsync(string qrcodeKey, CancellationToken cancellationToken);

    Task<bool> SaveLoginCookiesAsync(Uri redirectUri, CancellationToken cancellationToken);
}

internal sealed class LoginCoordinator : ILoginCoordinator
{
    public Task<LoginUrlOrigin?> RequestLoginUrlAsync(CancellationToken cancellationToken)
    {
        return RunAsync(LoginQr.GetLoginUrl, cancellationToken);
    }

    public Task<LoginStatus?> GetLoginStatusAsync(
        string qrcodeKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(qrcodeKey);
        return RunAsync(() => LoginQr.GetLoginStatus(qrcodeKey), cancellationToken);
    }

    public Task<bool> SaveLoginCookiesAsync(Uri redirectUri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(redirectUri);
        return RunAsync(() => LoginHelper.SaveLoginInfoCookies(redirectUri), cancellationToken);
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
