using DownKyi.Core.Storage;

namespace DownKyi.Core.BiliApi.Http;

public interface IBiliCookieProvider
{
    Task<IReadOnlyList<DownKyiCookie>> GetCookiesAsync(bool includeBuvid, CancellationToken cancellationToken = default);

    async Task<string> GetCookieHeaderAsync(bool includeBuvid, CancellationToken cancellationToken = default)
    {
        var cookies = await GetCookiesAsync(includeBuvid, cancellationToken).ConfigureAwait(false);
        return cookies.Count == 0
            ? string.Empty
            : string.Join("; ", cookies.Select(item => $"{item.Name}={item.Value}"));
    }
}
