using System;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Bilibili;
using DownKyi.Application.Diagnostics;
using DownKyi.Core.BiliApi.Login;
using DownKyi.Core.BiliApi.Login.Models;
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
    private readonly IBilibiliApiClient _client;

    public LoginCoordinator(
        ILogger<LoginCoordinator> logger,
        IBilibiliApiClient client)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<LoginUrlOrigin?> RequestLoginUrlAsync(
        CancellationToken cancellationToken)
    {
        var result = await _client.GetLoginUrlAsync(cancellationToken).ConfigureAwait(false);
        if (result == null)
        {
            _logger.LogWarningMessage("Bilibili login URL could not be created.");
        }

        return result;
    }

    public async Task<LoginStatus?> GetLoginStatusAsync(
        string qrcodeKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(qrcodeKey);
        var result = await _client.GetLoginStatusAsync(qrcodeKey, cancellationToken)
            .ConfigureAwait(false);
        if (result == null)
        {
            _logger.LogWarningMessage("Bilibili login status could not be read.");
        }

        return result;
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
