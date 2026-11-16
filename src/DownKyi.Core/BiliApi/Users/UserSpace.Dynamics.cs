using DownKyi.Application.Bilibili;
using DownKyi.Core.BiliApi.Users.Models;

namespace DownKyi.Core.BiliApi.Users;

public static partial class UserSpace
{
    private const string DynamicFeedFeatures =
        "itemOpusStyle,listOnlyfans,opusBigCover,onlyfansVote";

    public static async Task<SpaceDynamicPage> GetDynamicPageAsync(
        this IBilibiliApiClient client,
        long mid,
        string? offset = null,
        CancellationToken cancellationToken = default)
    {
        if (mid <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(mid), mid, "The uploader MID must be positive.");
        }

        var query = $"host_mid={mid}&offset={Uri.EscapeDataString(offset ?? string.Empty)}"
                    + $"&features={Uri.EscapeDataString(DynamicFeedFeatures)}";
        var url = $"https://api.bilibili.com/x/polymer/web-dynamic/v1/feed/space?{query}";
        var referer = $"https://space.bilibili.com/{mid}/dynamic";
        var origin = await BiliApiRequest.RequestJsonAsync<SpaceDynamicOrigin>(
            client,
            url,
            referer,
            nameof(GetDynamicPageAsync),
            "UserSpace",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var page = BiliApiRequest.RequirePayload(origin.Data);
        if (page.HasMore && string.IsNullOrWhiteSpace(page.Offset))
        {
            throw new BilibiliApiResponseException(
                nameof(GetDynamicPageAsync),
                "GetDynamicPageAsync returned a continuation page without an offset.");
        }

        if (page.HasMore && string.Equals(page.Offset, offset, StringComparison.Ordinal))
        {
            throw new BilibiliApiResponseException(
                nameof(GetDynamicPageAsync),
                "GetDynamicPageAsync returned the current offset as its continuation.");
        }

        return page;
    }
}
