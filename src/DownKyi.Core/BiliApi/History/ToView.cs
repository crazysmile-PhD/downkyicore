using System.Collections.Generic;
using System.Threading;
using DownKyi.Application.Bilibili;
using DownKyi.Core.BiliApi.History.Models;

namespace DownKyi.Core.BiliApi.History
{
    /// <summary>
    /// 稍后再看
    /// </summary>
    public static class ToView
    {
        /// <summary>
        /// 获取稍后再看视频列表
        /// </summary>
        /// <returns></returns>
        public static async Task<IReadOnlyList<ToViewList>?> GetToViewAsync(
            this IBilibiliApiClient client,
            CancellationToken cancellationToken = default)
        {
            const string url = "https://api.bilibili.com/x/v2/history/toview";
            const string referer = "https://www.bilibili.com";
            var toView = await BiliApiRequest.RequestJsonAsync<ToViewOrigin>(
                client,
                url,
                referer,
                nameof(GetToViewAsync),
                "ToView",
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return BiliApiRequest.RequirePayload(toView.Data).List;
        }
    }
}
