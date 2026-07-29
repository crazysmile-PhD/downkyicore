using DownKyi.Application.Bilibili;
using DownKyi.Application.Diagnostics;
using DownKyi.Core.BiliApi.Video.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Video;

public static class DynamicApi
{
    /// <summary>
    /// 获取分区最新视频列表
    /// </summary>
    /// <param name="rid">目标分区tid</param>
    /// <param name="pn">页码</param>
    /// <param name="ps">每页项数（最大50）</param>
    /// <returns></returns>
    public static async Task<IReadOnlyList<DynamicVideoView>?> RegionDynamicListAsync(
        this IBilibiliApiClient client,
        int rid,
        int pn = 1,
        int ps = 5,
        CancellationToken cancellationToken = default)
    {
        var url = $"https://api.bilibili.com/x/web-interface/dynamic/region?rid={rid}&pn={pn}&ps={ps}";
        const string referer = "https://www.bilibili.com";
        var dynamic = await BiliApiRequest.RequestJsonAsync<RegionDynamicOrigin>(
            client,
            url,
            referer,
            nameof(RegionDynamicListAsync),
            "Dynamic",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return BiliApiRequest.RequirePayload(dynamic.Data).Archives;
    }
}
