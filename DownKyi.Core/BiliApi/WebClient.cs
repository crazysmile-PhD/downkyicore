using DownKyi.Core.BiliApi.Http;

namespace DownKyi.Core.BiliApi;

public static class WebClient
{
    private static IBiliHttpClient _httpClient = BiliHttpClient.CreateDefault();

    public static string RequestWeb(
        string url,
        string? referer = null,
        string method = "GET",
        Dictionary<string, object?>? parameters = null,
        int retry = 2,
        bool json = false)
    {
        if (retry <= 0)
        {
            return string.Empty;
        }

        var result = _httpClient.SendAsync(url, referer, method, parameters, retry, json)
            .GetAwaiter()
            .GetResult();

        return result.IsSuccess ? result.Value ?? string.Empty : string.Empty;
    }

    public static void DownloadFile(string url, string destFile, string? referer = null)
    {
        var result = _httpClient.DownloadFileAsync(url, destFile, referer, retry: 1)
            .GetAwaiter()
            .GetResult();

        if (!result.IsSuccess)
        {
            throw result.Exception ?? new HttpRequestException(result.ErrorMessage, null, result.StatusCode);
        }
    }

    public static Stream RequestStream(string url, string? referer = null, string method = "GET")
    {
        var result = _httpClient.GetStreamAsync(url, referer, method, retry: 1)
            .GetAwaiter()
            .GetResult();

        if (result.IsSuccess && result.Value != null)
        {
            return result.Value;
        }

        throw result.Exception ?? new HttpRequestException(result.ErrorMessage, null, result.StatusCode);
    }

    internal static void SetHttpClientForTesting(IBiliHttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    internal static void ResetHttpClientForTesting()
    {
        _httpClient = BiliHttpClient.CreateDefault();
    }
}
