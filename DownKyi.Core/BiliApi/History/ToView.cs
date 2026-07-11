using DownKyi.Core.BiliApi.History.Models;
using DownKyi.Core.Logging;
using Newtonsoft.Json;
using Console = DownKyi.Core.Utils.Debugging.Console;

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
        public static IReadOnlyList<ToViewList>? GetToView()
        {
            const string url = "https://api.bilibili.com/x/v2/history/toview";
            const string referer = "https://www.bilibili.com";
            var toView = BiliApiRequest.RequestJson<ToViewOrigin>(
                url,
                referer,
                nameof(GetToView),
                "ToView");

            return toView?.Data?.List;
        }
    }
}
