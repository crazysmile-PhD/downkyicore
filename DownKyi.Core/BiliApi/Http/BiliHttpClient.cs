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
        cookieProvider ??= new DefaultBiliCookieProvider(new HttpClient(CreateDefaultHandler(proxySettingsProvider)), userAgentProvider);

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
        return SendStreamAsync(url, referer, method, retry, cancellationToken);
    }

    public async Task<ApiResult<bool>> DownloadFileAsync(
        string url,
        string destFile,
        string? referer = null,
        int retry = 2,
        CancellationToken cancellationToken = default)
    {
        var streamResult = await GetStreamAsync(url, referer, retry: retry, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!streamResult.IsSuccess || streamResult.Value == null)
        {
            return ApiResult<bool>.Failure(
                streamResult.ErrorMessage ?? "Download stream request failed.",
                streamResult.StatusCode,
                streamResult.Exception);
        }

        try
        {
            await using var inputStream = streamResult.Value;
            await using var outputStream = new FileStream(destFile, FileMode.Create, FileAccess.Write, FileShare.None);
            await inputStream.CopyToAsync(outputStream, cancellationToken).ConfigureAwait(false);
            return ApiResult<bool>.Success(true, streamResult.StatusCode);
        }
        catch (OperationCanceledException e) when (cancellationToken.IsCancellationRequested)
        {
            return ApiResult<bool>.Failure("Request was canceled.", streamResult.StatusCode, e);
        }
        catch (Exception e)
        {
            LogManager.Error(Tag, e);
            return ApiResult<bool>.Failure("Download file write failed.", streamResult.StatusCode, e);
        }
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

    private async Task<ApiResult<Stream>> SendStreamAsync(
        string url,
        string? referer,
        string method,
        int retry,
        CancellationToken cancellationToken)
    {
        var attempts = Math.Max(1, retry);
        ApiResult<Stream>? lastResult = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ApiResult<Stream>.Failure("Request was canceled.", exception: new OperationCanceledException(cancellationToken));
            }

            HttpRequestMessage? request = null;
            HttpResponseMessage? response = null;
            var keepResponseOpen = false;
            try
            {
                request = await CreateRequestAsync(
                    url,
                    referer,
                    method,
                    parameters: null,
                    json: false,
                    origin: MobileOrigin,
                    includeBuvid: false,
                    cancellationToken).ConfigureAwait(false);

                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    keepResponseOpen = true;
                    return ApiResult<Stream>.Success(new ResponseContentStream(stream, response, request), response.StatusCode);
                }

                lastResult = ApiResult<Stream>.Failure(
                    $"HTTP request failed with status code {(int)response.StatusCode} ({response.ReasonPhrase}).",
                    response.StatusCode);
            }
            catch (OperationCanceledException e) when (cancellationToken.IsCancellationRequested)
            {
                return ApiResult<Stream>.Failure("Request was canceled.", exception: e);
            }
            catch (TaskCanceledException e)
            {
                lastResult = ApiResult<Stream>.Failure("Request timed out.", exception: e);
            }
            catch (TimeoutException e)
            {
                lastResult = ApiResult<Stream>.Failure("Request timed out.", exception: e);
            }
            catch (HttpRequestException e)
            {
                lastResult = ApiResult<Stream>.Failure("Network request failed.", e.StatusCode, e);
            }
            catch (Exception e)
            {
                lastResult = ApiResult<Stream>.Failure("Unexpected HTTP request failure.", exception: e);
            }
            finally
            {
                if (!keepResponseOpen)
                {
                    response?.Dispose();
                    request?.Dispose();
                }
            }
        }

        LogFailure(lastResult);
        return lastResult ?? ApiResult<Stream>.Failure("HTTP request was not attempted.");
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

        LogFailure(lastResult);
        return lastResult ?? ApiResult<T>.Failure("HTTP request was not attempted.");
    }

    private static void LogFailure<T>(ApiResult<T>? result)
    {
        if (result?.Exception != null)
        {
            LogManager.Error(Tag, result.Exception);
        }
        else if (!string.IsNullOrEmpty(result?.ErrorMessage))
        {
            LogManager.Error(Tag, result.ErrorMessage);
        }
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

    private sealed class ResponseContentStream : Stream
    {
        private readonly Stream _inner;
        private readonly HttpResponseMessage _response;
        private readonly HttpRequestMessage _request;

        public ResponseContentStream(Stream inner, HttpResponseMessage response, HttpRequestMessage request)
        {
            _inner = inner;
            _response = response;
            _request = request;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(long value) => _inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return _inner.ReadAsync(buffer, cancellationToken);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return _inner.WriteAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _response.Dispose();
                _request.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            _response.Dispose();
            _request.Dispose();
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }

}
