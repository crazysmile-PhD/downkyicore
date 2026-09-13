using System.Net;
using System.Net.Http.Headers;
using DownKyi.Application.Bilibili;

namespace DownKyi.Infrastructure.Bilibili;

internal sealed class BilibiliLoginSessionFactory : IBilibiliLoginSessionFactory
{
    private readonly IHttpClientFactory _httpClientFactory;

    public BilibiliLoginSessionFactory(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory
                             ?? throw new ArgumentNullException(nameof(httpClientFactory));
    }

    public IBilibiliLoginSession Create(
        IReadOnlyList<BilibiliLoginCookie>? initialCookies = null)
    {
        var lifetime = new OwnedHttpClientLifetime(
            _httpClientFactory.CreateClient(
                BilibiliServiceCollectionExtensions.LoginHttpClientName));
        try
        {
            var transport = new BilibiliHttpTransport(lifetime, TimeProvider.System);
            return new BilibiliLoginSession(transport, lifetime, initialCookies);
        }
        catch
        {
            lifetime.Dispose();
            throw;
        }
    }

    private sealed class OwnedHttpClientLifetime
        : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client;
        private int _disposed;

        public OwnedHttpClientLifetime(HttpClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        public HttpClient CreateClient(string name) => _client;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _client.Dispose();
            }
        }
    }
}

internal sealed class BilibiliLoginSession : IBilibiliLoginSession
{
    private const int MaximumRedirects = 10;
    private readonly BilibiliHttpTransport _transport;
    private readonly IDisposable _lifetime;
    private readonly CookieContainer _cookies = new();
    private int _disposed;

    internal BilibiliLoginSession(
        BilibiliHttpTransport transport,
        IDisposable lifetime,
        IReadOnlyList<BilibiliLoginCookie>? initialCookies = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        foreach (var cookie in initialCookies ?? [])
        {
            AddInitialCookie(cookie);
        }
    }

    public async Task<BilibiliLoginResponse> GetAsync(
        BilibiliHttpRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var requestUri = RequireTrustedUri(new Uri(request.RequestAddress, UriKind.Absolute));
        var response = await SendAsync(
            requestUri,
            request.Referer,
            request.Attempts,
            requireContent: true,
            allowRedirectStatus: false,
            cancellationToken).ConfigureAwait(false);
        return new BilibiliLoginResponse(response.Content, GetCookies());
    }

    public async Task<IReadOnlyList<BilibiliLoginCookie>> FollowCallbackAsync(
        Uri callbackUri,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (IsTerminalLoginCallbackUri(callbackUri))
        {
            return GetCookies();
        }

        var currentUri = RequireTrustedUri(callbackUri);
        for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            var response = await SendAsync(
                currentUri,
                "https://www.bilibili.com/",
                attempts: 2,
                requireContent: false,
                allowRedirectStatus: true,
                cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is < 300 or >= 400)
            {
                return GetCookies();
            }

            if (redirect == MaximumRedirects || response.Location == null)
            {
                throw new BilibiliHttpRequestException(
                    "Bilibili login callback exceeded the allowed redirect chain.",
                    BilibiliHttpFailureKind.HttpStatus,
                    response.StatusCode);
            }

            var redirectUri = response.Location.IsAbsoluteUri
                ? response.Location
                : new Uri(currentUri, response.Location);
            if (IsTerminalLoginCallbackUri(redirectUri))
            {
                return GetCookies();
            }

            currentUri = RequireTrustedUri(redirectUri);
        }

        throw new InvalidOperationException("The Bilibili login redirect chain did not terminate.");
    }

    internal static bool IsTrustedBilibiliUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return uri.Scheme == Uri.UriSchemeHttps
               && uri.IsDefaultPort
               && string.IsNullOrEmpty(uri.UserInfo)
               && (uri.Host.Equals("bilibili.com", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTerminalLoginCallbackUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return uri.Scheme == Uri.UriSchemeHttps
               && uri.IsDefaultPort
               && string.IsNullOrEmpty(uri.UserInfo)
               && uri.Host.Equals("passport.biligame.com", StringComparison.OrdinalIgnoreCase)
               && uri.AbsolutePath.Equals("/x/passport-login/web/crossDomain", StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _lifetime.Dispose();
        }
    }

    private async Task<BilibiliHttpTextResponse> SendAsync(
        Uri requestUri,
        string? referer,
        int attempts,
        bool requireContent,
        bool allowRedirectStatus,
        CancellationToken cancellationToken)
    {
        var response = await _transport.GetResponseAsync(
            () => BuildRequest(requestUri, referer),
            attempts,
            requireContent,
            allowRedirectStatus,
            cancellationToken).ConfigureAwait(false);
        foreach (var setCookieHeader in response.SetCookieHeaders)
        {
            _cookies.SetCookies(requestUri, setCookieHeader);
        }

        return response;
    }

    private HttpRequestMessage BuildRequest(Uri requestUri, string? referer)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        if (referer != null)
        {
            request.Headers.Referrer = new Uri(referer, UriKind.Absolute);
        }

        var cookieHeader = _cookies.GetCookieHeader(requestUri);
        if (!string.IsNullOrWhiteSpace(cookieHeader))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private BilibiliLoginCookie[] GetCookies()
    {
        return _cookies.GetAllCookies()
            .Cast<Cookie>()
            .Where(cookie => !cookie.Expired
                             && IsPersistableCookieDomain(cookie.Domain)
                             && !string.IsNullOrWhiteSpace(cookie.Name)
                             && !string.IsNullOrEmpty(cookie.Value))
            .Select(cookie => new BilibiliLoginCookie(
                cookie.Name,
                cookie.Value,
                NormalizeDomain(cookie.Domain)))
            .ToArray();
    }

    private void AddInitialCookie(BilibiliLoginCookie cookie)
    {
        ArgumentNullException.ThrowIfNull(cookie);
        if (!IsBilibiliDomain(cookie.Domain))
        {
            throw new ArgumentException("Login cookies must belong to Bilibili.", nameof(cookie));
        }

        _cookies.Add(new Cookie(
            cookie.Name,
            cookie.Value,
            "/",
            NormalizeDomain(cookie.Domain)));
    }

    private static Uri RequireTrustedUri(Uri uri)
    {
        return IsTrustedBilibiliUri(uri)
            ? uri
            : throw new InvalidOperationException(
                "Bilibili login callbacks must use HTTPS on a Bilibili host.");
    }

    private static bool IsBilibiliDomain(string domain)
    {
        var normalized = domain.TrimStart('.');
        return normalized.Equals("bilibili.com", StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPersistableCookieDomain(string domain)
    {
        // The production cookie provider emits one shared header, so only cookies
        // explicitly scoped to the Bilibili parent domain may leave this session.
        return domain.TrimStart('.').Equals(
            "bilibili.com",
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDomain(string domain)
    {
        var normalized = domain.TrimStart('.');
        return $".{normalized}";
    }
}
