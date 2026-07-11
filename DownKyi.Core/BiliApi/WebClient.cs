using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using DownKyi.Core.BiliApi.Login;
using DownKyi.Core.Logging;
using DownKyi.Core.Settings;
using DownKyi.Core.Storage;

namespace DownKyi.Core.BiliApi;

public static class WebClient
{
    private static readonly HttpClient HttpClient;
    private static readonly SocketsHttpHandler SocketsHandler;
    private static int _resourcesDisposed;
    private static string? _bvuid3 = string.Empty;
    private static string? _bvuid4 = string.Empty;
    internal static Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>? SendOverrideForTests { get; set; }

    static WebClient()
    {
        SocketsHandler = CreateSocketsHandler();
        HttpClient = new HttpClient(SocketsHandler, disposeHandler: false);
        HttpClient.DefaultRequestHeaders.Add("User-Agent", SettingsManager.GetInstance().GetUserAgent());
        HttpClient.DefaultRequestHeaders.Add("accept-language", "zh-CN,zh;q=0.9,en-US;q=0.8,en;q=0.7");
    }

    public static void DisposeSharedResources()
    {
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0)
        {
            return;
        }

        HttpClient.Dispose();
        SocketsHandler.Dispose();
    }

    private static SocketsHttpHandler CreateSocketsHandler()
    {
        var socketsHandler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(8)
        };
        switch (SettingsManager.GetInstance().GetNetworkProxy())
        {
            case NetworkProxy.None:
                socketsHandler.UseProxy = false;
                socketsHandler.Proxy = null;
                break;
            case NetworkProxy.System:
                socketsHandler.UseProxy = true;
                socketsHandler.Proxy = HttpClient.DefaultProxy;
                break;
            case NetworkProxy.Custom:
                {
                    try
                    {
                        socketsHandler.UseProxy = true;
                        socketsHandler.Proxy = new WebProxy(SettingsManager.GetInstance().GetCustomProxy());
                    }
                    catch (UriFormatException e)
                    {
                        socketsHandler.UseProxy = false;
                        socketsHandler.Proxy = null;
                        LogManager.Error(nameof(WebClient), e);
                    }
                }
                break;
        }

        return socketsHandler;
    }

    internal class SpiOrigin
    {
        [JsonPropertyName("data")] public Spi? Data { get; init; }
        public int Code { get; init; }
        public string? Message { get; init; }
    }

    internal class Spi
    {
        [JsonPropertyName("b_3")] public string? Bvuid3 { get; set; }
        [JsonPropertyName("b_4")] public string? Bvuid4 { get; set; }
    }

    private static void GetBuvid(CancellationToken cancellationToken = default)
    {
        const string url = "https://api.bilibili.com/x/frontend/finger/spi";
        var response = RequestWeb(url, cancellationToken: cancellationToken);
        var spi = JsonSerializer.Deserialize<SpiOrigin>(response);
        _bvuid3 = spi?.Data?.Bvuid3;
        _bvuid4 = spi?.Data?.Bvuid4;
    }

    public static string RequestWeb(
        string url,
        string? referer = null,
        string method = "GET",
        Dictionary<string, object?>? parameters = null,
        int retry = 2,
        bool json = false,
        CancellationToken cancellationToken = default)
    {
        Exception? lastError = null;
        var attempts = Math.Max(1, retry);
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(_bvuid3) && url != "https://api.bilibili.com/x/frontend/finger/spi")
                {
                    GetBuvid(cancellationToken);
                }

                using var request = BuildRequest(url, referer, method, parameters, json);
                using var response = SendRequest(request, cancellationToken);
                response.EnsureSuccessStatusCode();

                using var stream = response.Content.ReadAsStream(cancellationToken);
                using var reader = new StreamReader(stream);
                var content = reader.ReadToEnd();
                if (string.IsNullOrWhiteSpace(content))
                {
                    throw new HttpRequestException(
                        $"Request returned an empty response: {LogManager.SanitizeForDiagnostics(url)}");
                }

                return content;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException e)
            {
                lastError = e;
                LogManager.Error(nameof(RequestWeb), e);
            }
            catch (IOException e)
            {
                lastError = e;
                LogManager.Error(nameof(RequestWeb), e);
            }
            catch (InvalidOperationException e)
            {
                lastError = e;
                LogManager.Error(nameof(RequestWeb), e);
            }

            if (attempt < attempts)
            {
                WaitBeforeRetry(attempt, cancellationToken);
            }
        }

        throw new HttpRequestException(
            $"Request failed after {attempts} attempts: {LogManager.SanitizeForDiagnostics(url)}",
            lastError);
    }

    internal static void ResetBuvidForTests()
    {
        _bvuid3 = string.Empty;
        _bvuid4 = string.Empty;
    }

    internal static void SetBuvidForTests(
        string buvid3 = "test-buvid3",
        string buvid4 = "test-buvid4")
    {
        _bvuid3 = buvid3;
        _bvuid4 = buvid4;
    }

    internal static void ClearTestOverrides()
    {
        SendOverrideForTests = null;
        ResetBuvidForTests();
    }

    internal static TimeSpan GetRetryDelayForTests(int attempt)
    {
        return GetRetryDelay(attempt);
    }

    internal static string BuildRequestUrlForTests(string url, string method, Dictionary<string, object?>? parameters)
    {
        return BuildRequestUrl(url, method, parameters);
    }

    private static HttpRequestMessage BuildRequest(
        string url,
        string? referer,
        string method,
        Dictionary<string, object?>? parameters,
        bool json)
    {
        var requestUrl = BuildRequestUrl(url, method, parameters);
        var request = new HttpRequestMessage(new HttpMethod(method), requestUrl);

        if (referer != null)
        {
            request.Headers.Referrer = new Uri(referer);
        }

        if (!url.Contains("getLogin"))
        {
            request.Headers.Add("origin", "https://www.bilibili.com");

            var cookies = LoginHelper.GetLoginInfoCookies();

            if (!string.IsNullOrEmpty(_bvuid3))
            {
                cookies.Add(new DownKyiCookie("buvid3", HttpUtility.UrlEncode(_bvuid3)));
            }

            if (!string.IsNullOrEmpty(_bvuid4))
            {
                cookies.Add(new DownKyiCookie("buvid4", HttpUtility.UrlEncode(_bvuid4)));
            }

            var cookieHeader = LoginHelper.BuildCookieHeader(cookies);
            if (!string.IsNullOrEmpty(cookieHeader))
            {
                request.Headers.Add("cookie", cookieHeader);
            }
        }

        if (method == "POST" && parameters != null)
        {
            if (json)
            {
                request.Content = new StringContent(
                    JsonSerializer.Serialize(parameters),
                    System.Text.Encoding.UTF8,
                    "application/json");
            }
            else
            {
                request.Content = new FormUrlEncodedContent(
                    parameters.Select(item =>
                        new KeyValuePair<string, string>(item.Key, item.Value?.ToString() ?? "")));
            }
        }

        return request;
    }

    private static HttpResponseMessage SendRequest(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return SendOverrideForTests?.Invoke(request, cancellationToken)
               ?? HttpClient.Send(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static string BuildRequestUrl(string url, string method, Dictionary<string, object?>? parameters)
    {
        if (method == "POST" || parameters == null || parameters.Count == 0)
        {
            return url;
        }

        var query = string.Join("&", parameters.Select(kvp =>
            $"{HttpUtility.UrlEncode(kvp.Key)}={HttpUtility.UrlEncode(kvp.Value?.ToString() ?? string.Empty)}"));

        return url.Contains("?", StringComparison.Ordinal)
            ? $"{url}&{query}"
            : $"{url}?{query}";
    }

    private static void WaitBeforeRetry(int attempt, CancellationToken cancellationToken)
    {
        var delay = GetRetryDelay(attempt);
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        cancellationToken.WaitHandle.WaitOne(delay);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static TimeSpan GetRetryDelay(int attempt)
    {
        var delayMilliseconds = Math.Clamp(attempt * 250, 250, 2000);
        return TimeSpan.FromMilliseconds(delayMilliseconds);
    }

    public static void DownloadFile(string url, string destFile, string? referer = null, CancellationToken cancellationToken = default)
    {
        using var fs = File.Create(destFile);
        using var stream = RequestStream(url, referer, cancellationToken: cancellationToken);
        stream.CopyTo(fs);
    }

    public static Stream RequestStream(string url, string? referer = null, string method = "GET", CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = new HttpRequestMessage(new HttpMethod(method), url);

        if (referer != null)
        {
            request.Headers.Referrer = new Uri(referer);
        }

        if (!url.Contains("getLogin"))
        {
            request.Headers.Add("origin", "https://m.bilibili.com");
            var cookies = LoginHelper.GetLoginInfoCookiesString();
            if (cookies is not "")
            {
                request.Headers.Add("cookie", cookies);
            }
        }

        var response = HttpClient.Send(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        try
        {
            response.EnsureSuccessStatusCode();
            return new ResponseStream(response.Content.ReadAsStream(cancellationToken), response, request);
        }
        catch
        {
            response.Dispose();
            request.Dispose();
            throw;
        }
    }

    private sealed class ResponseStream : Stream
    {
        private readonly Stream _inner;
        private readonly HttpResponseMessage _response;
        private readonly HttpRequestMessage _request;

        public ResponseStream(Stream inner, HttpResponseMessage response, HttpRequestMessage request)
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
