using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using DownKyi.Core.BiliApi.Login;
using DownKyi.Core.Logging;
using DownKyi.Core.Storage;

namespace DownKyi.Core.BiliApi.Http;

public class DefaultBiliCookieProvider : IBiliCookieProvider
{
    public const string SpiUrl = "https://api.bilibili.com/x/frontend/finger/spi";
    private const string Tag = "DefaultBiliCookieProvider";
    private const string AcceptLanguage = "zh-CN,zh;q=0.9,en-US;q=0.8,en;q=0.7";
    private const string WebOrigin = "https://www.bilibili.com";

    private readonly HttpClient _httpClient;
    private readonly IUserAgentProvider _userAgentProvider;
    private string? _buvid3 = string.Empty;
    private string? _buvid4 = string.Empty;
    private bool _hasRequestedBuvid;

    public DefaultBiliCookieProvider()
        : this(new HttpClient(), new SettingsUserAgentProvider())
    {
    }

    public DefaultBiliCookieProvider(HttpClient httpClient, IUserAgentProvider userAgentProvider)
    {
        _httpClient = httpClient;
        _userAgentProvider = userAgentProvider;
    }

    public async Task<IReadOnlyList<DownKyiCookie>> GetCookiesAsync(bool includeBuvid, CancellationToken cancellationToken = default)
    {
        var cookies = LoginHelper.GetLoginInfoCookies()
            .Select(item => new DownKyiCookie(item.Name, item.Value, item.Domain))
            .ToList();

        if (includeBuvid)
        {
            await EnsureBuvidAsync(cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(_buvid3))
            {
                cookies.Add(new DownKyiCookie("buvid3", HttpUtility.UrlEncode(_buvid3)));
            }

            if (!string.IsNullOrEmpty(_buvid4))
            {
                cookies.Add(new DownKyiCookie("buvid4", HttpUtility.UrlEncode(_buvid4)));
            }
        }

        return cookies;
    }

    private async Task EnsureBuvidAsync(CancellationToken cancellationToken)
    {
        if (_hasRequestedBuvid)
        {
            return;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, SpiUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", _userAgentProvider.GetUserAgent());
            request.Headers.TryAddWithoutValidation("accept-language", AcceptLanguage);
            request.Headers.TryAddWithoutValidation("origin", WebOrigin);

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LogManager.Error(Tag, $"Get buvid failed: {(int)response.StatusCode} {response.ReasonPhrase}");
                return;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var spi = await JsonSerializer.DeserializeAsync<SpiOrigin>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            _buvid3 = spi?.Data?.Buvid3;
            _buvid4 = spi?.Data?.Buvid4;
            _hasRequestedBuvid = !string.IsNullOrEmpty(_buvid3) || !string.IsNullOrEmpty(_buvid4);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            LogManager.Error(Tag, e);
        }
    }

    private class SpiOrigin
    {
        [JsonPropertyName("data")] public Spi? Data { get; init; }
    }

    private class Spi
    {
        [JsonPropertyName("b_3")] public string? Buvid3 { get; init; }

        [JsonPropertyName("b_4")] public string? Buvid4 { get; init; }
    }
}
