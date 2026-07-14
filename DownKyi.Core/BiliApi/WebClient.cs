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
    private static readonly SocketsHttpHandler SocketsHandler = CreateSocketsHandler();
    private static readonly HttpClient HttpClient = CreateHttpClient(SocketsHandler);
    private static BilibiliHttpClient _client = new(HttpClient);
    private static int _resourcesDisposed;
    private static string? _bvuid3 = string.Empty;
    private static string? _bvuid4 = string.Empty;
    internal static Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>? SendOverrideForTests { get; set; }

    public static void Configure(BilibiliHttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        Volatile.Write(ref _client, client);
    }

    private static HttpClient CreateHttpClient(SocketsHttpHandler socketsHandler)
    {
        var httpClient = new HttpClient(socketsHandler, disposeHandler: false);
        ConfigureDefaults(httpClient);
        return httpClient;
    }

    internal static void ConfigureDefaults(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        httpClient.DefaultRequestHeaders.Add("User-Agent", SettingsManager.Instance.GetUserAgent());
        httpClient.DefaultRequestHeaders.Add("accept-language", "zh-CN,zh;q=0.9,en-US;q=0.8,en;q=0.7");
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

    internal static SocketsHttpHandler CreateSocketsHandler()
    {
        var socketsHandler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(8)
        };
        switch (SettingsManager.Instance.GetNetworkProxy())
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
                        socketsHandler.Proxy = new WebProxy(SettingsManager.Instance.GetCustomProxy());
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
        var spi = JsonSerializer.Deserialize(response, BilibiliWebJsonContext.Default.SpiOrigin);
        _bvuid3 = spi?.Data?.Bvuid3;
        _bvuid4 = spi?.Data?.Bvuid4;
    }

    public static string RequestWeb(
        string requestAddress,
        string? referer = null,
        string method = "GET",
        Dictionary<string, object?>? parameters = null,
        int retry = 2,
        bool json = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestAddress);

        var attempts = Math.Max(1, retry);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(_bvuid3) && requestAddress != "https://api.bilibili.com/x/frontend/finger/spi")
            {
                GetBuvid(cancellationToken);
            }

            return Volatile.Read(ref _client).Send(
                () => BuildRequest(requestAddress, referer, method, parameters, json),
                attempts,
                SendOverrideForTests,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException)
        {
            LogManager.Error(nameof(RequestWeb), e);
            throw;
        }
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
        return BilibiliHttpClient.GetBackoff(attempt);
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

        if (!url.Contains("getLogin", StringComparison.Ordinal))
        {
            request.Headers.Add("origin", "https://www.bilibili.com");

            var cookies = LoginHelper.GetLoginInfoCookies().ToList();

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

    private static string BuildRequestUrl(string url, string method, Dictionary<string, object?>? parameters)
    {
        if (method == "POST" || parameters == null || parameters.Count == 0)
        {
            return url;
        }

        var query = string.Join("&", parameters.Select(kvp =>
            $"{HttpUtility.UrlEncode(kvp.Key)}={HttpUtility.UrlEncode(kvp.Value?.ToString() ?? string.Empty)}"));

        return url.Contains('?', StringComparison.Ordinal)
            ? $"{url}&{query}"
            : $"{url}?{query}";
    }

    public static void DownloadFile(string sourceAddress, string destFile, string? referer = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(destFile);
        var temporaryFile = $"{destFile}.download";
        try
        {
            using (var output = new FileStream(
                       temporaryFile,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 81920,
                       FileOptions.SequentialScan))
            using (var input = RequestStream(sourceAddress, referer, cancellationToken: cancellationToken))
            {
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryFile, destFile, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temporaryFile);
            }
            catch (IOException e)
            {
                LogManager.Debug(nameof(DownloadFile), $"Temporary download cleanup failed: {e.Message}");
            }
            catch (UnauthorizedAccessException e)
            {
                LogManager.Debug(nameof(DownloadFile), $"Temporary download cleanup was denied: {e.Message}");
            }

            throw;
        }
    }

    public static Stream RequestStream(string requestAddress, string? referer = null, string method = "GET", CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestAddress);

        cancellationToken.ThrowIfCancellationRequested();
        var request = new HttpRequestMessage(new HttpMethod(method), requestAddress);

        if (referer != null)
        {
            request.Headers.Referrer = new Uri(referer);
        }

        if (!requestAddress.Contains("getLogin", StringComparison.Ordinal))
        {
            request.Headers.Add("origin", "https://m.bilibili.com");
            var cookies = LoginHelper.GetLoginInfoCookiesString();
            if (cookies is not "")
            {
                request.Headers.Add("cookie", cookies);
            }
        }

        var response = Volatile.Read(ref _client)
            .SendResponse(request, SendOverrideForTests, cancellationToken);
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
