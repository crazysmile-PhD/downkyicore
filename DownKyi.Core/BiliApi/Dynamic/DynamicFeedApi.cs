using DownKyi.Application.Bilibili;
using DownKyi.Core.BiliApi.Dynamic.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Dynamic;

public static class DynamicFeedApi
{
    private const string FeedUrl =
        "https://api.bilibili.com/x/polymer/web-dynamic/v1/feed/all?type=all&platform=web";

    public static async Task<DynamicFeedData> GetDynamicFeedAsync(
        this IBilibiliApiClient client,
        string? offset = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var url = string.IsNullOrWhiteSpace(offset)
            ? FeedUrl
            : $"{FeedUrl}&offset={Uri.EscapeDataString(offset)}";
        var origin = await BiliApiRequest.RequestJsonAsync<DynamicFeedOrigin>(
            client,
            url,
            "https://t.bilibili.com/",
            nameof(GetDynamicFeedAsync),
            nameof(DynamicFeedApi),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return BiliApiRequest.RequirePayload(origin.Data);
    }

    internal static DynamicFeedData? ParseResponse(string response)
    {
        var origin = JsonConvert.DeserializeObject<DynamicFeedOrigin>(response);
        return origin is { Code: 0 } ? origin.Data : null;
    }
}
