namespace DownKyi.Core.BiliApi.Http;

public interface IBiliHttpClient
{
    Task<ApiResult<string>> GetStringAsync(
        string url,
        string? referer = null,
        Dictionary<string, object?>? parameters = null,
        int retry = 2,
        CancellationToken cancellationToken = default);

    Task<ApiResult<Stream>> GetStreamAsync(
        string url,
        string? referer = null,
        string method = "GET",
        int retry = 2,
        CancellationToken cancellationToken = default);

    Task<ApiResult<string>> SendAsync(
        string url,
        string? referer = null,
        string method = "GET",
        Dictionary<string, object?>? parameters = null,
        int retry = 2,
        bool json = false,
        CancellationToken cancellationToken = default);
}
