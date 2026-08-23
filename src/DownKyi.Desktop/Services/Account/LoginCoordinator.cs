using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Bilibili;
using DownKyi.Application.Diagnostics;
using DownKyi.Core.BiliApi.Login;
using DownKyi.Core.BiliApi.Login.Models;
using DownKyi.Core.BiliApi.Users;
using DownKyi.Core.Storage;
using DownKyi.Core.Utils;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Account;

internal interface ILoginCoordinator : IDisposable
{
    Task<LoginUrlOrigin?> RequestLoginUrlAsync(CancellationToken cancellationToken);

    Task<LoginStatusResult?> GetLoginStatusAsync(string qrcodeKey, CancellationToken cancellationToken);

    Task<bool> SaveLoginCookiesAsync(
        LoginStatusResult loginStatus,
        Uri redirectUri,
        CancellationToken cancellationToken);
}

internal sealed class LoginCoordinator : ILoginCoordinator
{
    private readonly ILogger<LoginCoordinator> _logger;
    private readonly IBilibiliApiClient _client;
    private readonly IBilibiliLoginSessionFactory _sessionFactory;
    private readonly object _sessionLock = new();
    private IBilibiliLoginSession? _session;
    private int _disposed;

    public LoginCoordinator(
        ILogger<LoginCoordinator> logger,
        IBilibiliApiClient client,
        IBilibiliLoginSessionFactory sessionFactory)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
    }

    public async Task<LoginUrlOrigin?> RequestLoginUrlAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var replacement = _sessionFactory.Create();
        var previous = InstallSession(replacement);
        previous?.Dispose();
        var result = await replacement.GetLoginUrlAsync(cancellationToken).ConfigureAwait(false);
        if (result == null)
        {
            _logger.LogWarningMessage("Bilibili login URL could not be created.");
        }

        return result;
    }

    public async Task<LoginStatusResult?> GetLoginStatusAsync(
        string qrcodeKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(qrcodeKey);
        var session = GetSession();
        var result = await session.GetLoginStatusAsync(qrcodeKey, cancellationToken)
            .ConfigureAwait(false);
        if (result == null)
        {
            _logger.LogWarningMessage("Bilibili login status could not be read.");
        }

        return result;
    }

    public async Task<bool> SaveLoginCookiesAsync(
        LoginStatusResult loginStatus,
        Uri redirectUri,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(loginStatus);
        ArgumentNullException.ThrowIfNull(redirectUri);
        var callbackCookies = await GetSession()
            .FollowCallbackAsync(redirectUri, cancellationToken)
            .ConfigureAwait(false);
        var cookies = MergeCookies(
            loginStatus.Cookies,
            callbackCookies,
            ObjectHelper.ParseCookie(redirectUri));
        var previousCookies = LoginHelper.GetLoginInfoCookies();
        var saved = await RunAsync(
            () => LoginHelper.SaveLoginInfoCookies(cookies),
            cancellationToken).ConfigureAwait(false);
        if (!saved)
        {
            _logger.LogWarningMessage("Bilibili login cookies could not be persisted.");
            return false;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var navigation = await _client.GetUserInfoForNavigationAsync(cancellationToken)
                .ConfigureAwait(false);
            if (navigation?.IsLogin == true)
            {
                return true;
            }
        }
        catch (Exception e) when (e is OperationCanceledException or HttpRequestException
            or InvalidOperationException or ArgumentException or Newtonsoft.Json.JsonException)
        {
            await RestoreCookiesAsync(previousCookies).ConfigureAwait(false);
            ExceptionDispatchInfo.Capture(e).Throw();
        }

        await RestoreCookiesAsync(previousCookies).ConfigureAwait(false);

        _logger.LogWarningMessage("Persisted Bilibili login cookies failed account validation.");
        return false;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            ReplaceSession(null)?.Dispose();
        }
    }

    private IBilibiliLoginSession GetSession()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        lock (_sessionLock)
        {
            return _session ?? throw new InvalidOperationException(
                "A Bilibili login session has not been initialized.");
        }
    }

    private IBilibiliLoginSession? ReplaceSession(IBilibiliLoginSession? replacement)
    {
        lock (_sessionLock)
        {
            var previous = _session;
            _session = replacement;
            return previous;
        }
    }

    private IBilibiliLoginSession? InstallSession(IBilibiliLoginSession replacement)
    {
        lock (_sessionLock)
        {
            if (_disposed != 0)
            {
                replacement.Dispose();
                throw new ObjectDisposedException(nameof(LoginCoordinator));
            }

            var previous = _session;
            _session = replacement;
            return previous;
        }
    }

    private static DownKyiCookie[] MergeCookies(
        IEnumerable<BilibiliLoginCookie> pollCookies,
        IEnumerable<BilibiliLoginCookie> callbackCookies,
        IEnumerable<DownKyiCookie> callbackParameters)
    {
        var cookies = new Dictionary<string, DownKyiCookie>(StringComparer.OrdinalIgnoreCase);
        foreach (var cookie in callbackParameters)
        {
            if (!string.IsNullOrWhiteSpace(cookie.Name) && !string.IsNullOrEmpty(cookie.Value))
            {
                cookies[cookie.Name] = cookie;
            }
        }

        foreach (var cookie in pollCookies.Concat(callbackCookies))
        {
            if (!string.IsNullOrWhiteSpace(cookie.Name) && !string.IsNullOrEmpty(cookie.Value))
            {
                cookies[cookie.Name] = new DownKyiCookie(
                    cookie.Name,
                    cookie.Value,
                    cookie.Domain,
                    isWireValue: true);
            }
        }

        return cookies.Values.ToArray();
    }

    private static bool RestoreCookies(IReadOnlyList<DownKyiCookie> cookies)
    {
        return cookies.Count > 0
            ? LoginHelper.SaveLoginInfoCookies(cookies)
            : LoginHelper.DeleteLoginInfoCookies();
    }

    private static async Task RestoreCookiesAsync(IReadOnlyList<DownKyiCookie> cookies)
    {
        var restored = await RunAsync(
            () => RestoreCookies(cookies),
            CancellationToken.None).ConfigureAwait(false);
        if (!restored)
        {
            throw new IOException("The previous Bilibili login cookies could not be restored.");
        }
    }

    private static Task<T> RunAsync<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return operation();
        }, cancellationToken);
    }
}
