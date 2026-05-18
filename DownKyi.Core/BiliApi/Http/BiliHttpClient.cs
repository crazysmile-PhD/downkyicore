using System.Net;
using System.Text;
using System.Text.Json;
using DownKyi.Core.Logging;
using DownKyi.Core.Settings;

namespace DownKyi.Core.BiliApi.Http;

public class BiliHttpClient : IBiliHttpClient
{
    private const string Tag = "BiliHttpClient";
    private const string WebOrigin = "https://www.bilibili.com";
    private const string MobileOrigin = "https://m.bilibili.com";
    private const string AcceptLanguage = "zh-CN,zh;q=0.9,en-US;q=0.8,en;q=0.7";

    private readonly HttpClient _httpClient;
    private readonly IBiliCookieProvider _cookieProvider;
    private readonly IUserAgentProvider _userAgentProvider;

    public BiliHttpClient(HttpClient httpClient, IBiliCookieProvider cookieProvider, IUserAgentProvider userAgentProvider)
    {
        _httpClient = httpClient;
        _cookieProvider = cookieProvider;
        _userAgentProvider = userAgentProvider;
    }

    public static BiliHttpClient CreateDefault(
        IProxySettingsProvider? proxySettingsProvider = null,
        IBiliCookieProvider? cookieProvider = null,
        IUserAgentProvider? userAgentProvider = null)
    {
        proxySettingsProvider ??= new SettingsProxySettingsProvider();
        userAgentProvider ??= new SettingsUserAgentProvider();

        var handler = CreateDefaultHandler(proxySettingsProvider);
        var httpClient = new HttpClient(handler);
        cookieProvider ??= new DefaultBiliCookieProvider(new HttpClient(CreateDefaultHandler(proxySettingsProvider)));

        return new BiliHttpClient(httpClient, cookieProvider, userAgentProvider);
    }

    public Task<ApiResult<string>> GetStringAsync(
        string url,
        string? referer = null,
        Dictionary<string, object?>? parameters = null,
        int retry = 2,
        CancellationToken cancellationToken = default)
    {
        return SendAsync(url, referer, "GET", parameters, retry, false, cancellationToken);
    }

    public Task<ApiResult<Stream>> GetStreamAsync(
        string url,
        string? referer = null,
        string method = "GET",
        int retry = 2,
        CancellationToken cancellationToken = default)
    {
        return SendCoreAsync(
            url,
            referer,
            method,
            parameters: null,
            retry,
            json: false,
            origin: MobileOrigin,
            includeBuvid: false,
            async (response, token) =>
            {
                var memoryStream = new MemoryStream();
                await response.Content.CopyToAsync(memoryStream, token).ConfigureAwait(false);
                memoryStream.Position = 0;
                return (Stream)memoryStream;
            },
            cancellationToken);
    }

    public Task<ApiResult<string>> SendAsync(
        string url,
        string? referer = null,
        string method = "GET",
        Dictionary<string, object?>? parameters = null,
        int retry = 2,
        bool json = false,
        CancellationToken cancellationToken = default)
    {
        return SendCoreAsync(
            url,
            referer,
            method,
            parameters,
            retry,
            json,
            WebOrigin,
            includeBuvid: !string.Equals(url, DefaultBiliCookieProvider.SpiUrl, StringComparison.Ordinal),
            async (response, token) => await response.Content.ReadAsStringAsync(token).ConfigureAwait(false),
            cancellationToken);
    }

    private static SocketsHttpHandler CreateDefaultHandler(IProxySettingsProvider proxySettingsProvider)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(3)
        };

        switch (proxySettingsProvider.GetNetworkProxy())
        {
            case NetworkProxy.None:
                handler.UseProxy = false;
                handler.Proxy = null;
                break;
            case NetworkProxy.System:
                handler.UseProxy = true;
                handler.Proxy = HttpClient.DefaultProxy;
                break;
            case NetworkProxy.Custom:
                ConfigureCustomProxy(handler, proxySettingsProvider.GetCustomProxy());
                break;
        }

        return handler;
    }

    private static void ConfigureCustomProxy(SocketsHttpHandler handler, string customProxy)
    {
        try
        {
            handler.UseProxy = true;
            handler.Proxy = new WebProxy(customProxy);
        }
        catch (Exception e)
        {
            handler.UseProxy = false;
            handler.Proxy = null;
            LogManager.Error(Tag, e);
        }
    }

    private async Task<ApiResult<T>> SendCoreAsync<T>(
        string url,
        string? referer,
        string method,
        Dictionary<string, object?>? parameters,
        int retry,
        bool json,
        string origin,
        bool includeBuvid,
        Func<HttpResponseMessage, CancellationToken, Task<T>> readContentAsync,
        CancellationToken cancellationToken)
    {
        var attempts = Math.Max(1, retry);
        ApiResult<T>? lastResult = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ApiResult<T>.Failure("Request was canceled.", exception: new OperationCanceledException(cancellationToken));
            }

            try
            {
                using var request = await CreateRequestAsync(url, referer, method, parameters, json, origin, includeBuvid, cancellationToken).ConfigureAwait(false);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    var value = await readContentAsync(response, cancellationToken).ConfigureAwait(false);
                    return ApiResult<T>.Success(value, response.StatusCode);
                }

                lastResult = ApiResult<T>.Failure(
                    $"HTTP request failed with status code {(int)response.StatusCode} ({response.ReasonPhrase}).",
                    response.StatusCode);
            }
            catch (OperationCanceledException e) when (cancellationToken.IsCancellationRequested)
            {
                return ApiResult<T>.Failure("Request was canceled.", exception: e);
            }
            catch (TaskCanceledException e)
            {
                lastResult = ApiResult<T>.Failure("Request timed out.", exception: e);
            }
            catch (TimeoutException e)
            {
                lastResult = ApiResult<T>.Failure("Request timed out.", exception: e);
            }
            catch (HttpRequestException e)
            {
                lastResult = ApiResult<T>.Failure("Network request failed.", e.StatusCode, e);
            }
            catch (Exception e)
            {
                lastResult = ApiResult<T>.Failure("Unexpected HTTP request failure.", exception: e);
            }
        }

        if (lastResult?.Exception != null)
        {
            LogManager.Error(Tag, lastResult.Exception);
        }
        else if (!string.IsNullOrEmpty(lastResult?.ErrorMessage))
        {
            LogManager.Error(Tag, lastResult.ErrorMessage);
        }

        return lastResult ?? ApiResult<T>.Failure("HTTP request was not attempted.");
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(
        string url,
        string? referer,
        string method,
        Dictionary<string, object?>? parameters,
        bool json,
        string origin,
        bool includeBuvid,
        CancellationToken cancellationToken)
    {
        var httpMethod = new HttpMethod(method);
        var requestUri = ShouldWriteParametersToQuery(httpMethod, parameters)
            ? BuildUri(url, parameters)
            : new Uri(url);

        var request = new HttpRequestMessage(httpMethod, requestUri);
        AddHeaders(request, url, referer, origin);
        await AddCookiesAsync(request, url, includeBuvid, cancellationToken).ConfigureAwait(false);
        AddContent(request, httpMethod, parameters, json);

        return request;
    }

    private static bool ShouldWriteParametersToQuery(HttpMethod method, Dictionary<string, object?>? parameters)
    {
        return parameters is { Count: > 0 } && method != HttpMethod.Post;
    }

    private static Uri BuildUri(string url, Dictionary<string, object?>? parameters)
    {
        if (parameters is not { Count: > 0 })
        {
            return new Uri(url);
        }

        var builder = new UriBuilder(url);
        var existingQuery = builder.Query;
        if (existingQuery.StartsWith("?", StringComparison.Ordinal))
        {
            existingQuery = existingQuery[1..];
        }

        var newQuery = string.Join("&", parameters.Select(kvp =>
            $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value?.ToString() ?? string.Empty)}"));

        builder.Query = string.IsNullOrEmpty(existingQuery)
            ? newQuery
            : $"{existingQuery}&{newQuery}";

        return builder.Uri;
    }

    private void AddHeaders(HttpRequestMessage request, string originalUrl, string? referer, string origin)
    {
        if (referer != null)
        {
            request.Headers.Referrer = new Uri(referer);
        }

        if (!originalUrl.Contains("getLogin"))
        {
            request.Headers.TryAddWithoutValidation("origin", origin);
        }

        request.Headers.TryAddWithoutValidation("User-Agent", _userAgentProvider.GetUserAgent());
        request.Headers.TryAddWithoutValidation("accept-language", AcceptLanguage);
    }

    private async Task AddCookiesAsync(HttpRequestMessage request, string originalUrl, bool includeBuvid, CancellationToken cancellationToken)
    {
        if (originalUrl.Contains("getLogin"))
        {
            return;
        }

        var cookieHeader = await _cookieProvider.GetCookieHeaderAsync(includeBuvid, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(cookieHeader))
        {
            request.Headers.TryAddWithoutValidation("cookie", cookieHeader);
        }
    }

    private static void AddContent(HttpRequestMessage request, HttpMethod method, Dictionary<string, object?>? parameters, bool json)
    {
        if (method != HttpMethod.Post || parameters == null)
        {
            return;
        }

        request.Content = json
            ? new StringContent(JsonSerializer.Serialize(parameters), Encoding.UTF8, "application/json")
            : new FormUrlEncodedContent(parameters.Select(item => new KeyValuePair<string, string>(item.Key, item.Value?.ToString() ?? string.Empty)));
    }
}
