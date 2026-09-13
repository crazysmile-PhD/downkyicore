using Bilibili.Community.Service.Dm.V1;
using DownKyi.Application.Bilibili;
using DownKyi.Core.BiliApi.DanmakuApi.Models;
using DownKyi.Core.Storage;
using Google.Protobuf;

namespace DownKyi.Core.BiliApi.DanmakuApi;

public static class DanmakuProtobuf
{
    private const string Referer = "https://www.bilibili.com";

    private static async Task<int> GetSegmentCountAsync(
        IBilibiliApiClient client,
        long avid,
        long cid,
        CancellationToken cancellationToken)
    {
        var url = $"https://api.bilibili.com/x/v2/dm/web/view?type=1&oid={cid}&pid={avid}";
        var request = new BilibiliHttpRequest(url, Referer);
        var input = await client.OpenReadAsync(request, cancellationToken).ConfigureAwait(false);
        await using (input.ConfigureAwait(false))
        {
            var view = DmWebViewReply.Parser.ParseFrom(input);
            if (view.DmSge == null || view.DmSge.Total < 0 || view.DmSge.Total >= int.MaxValue)
            {
                throw new InvalidDataException(
                    "The danmaku metadata response does not contain a valid segment bound.");
            }

            return checked((int)view.DmSge.Total);
        }
    }

    /// <summary>
    /// 下载6分钟内的弹幕，返回弹幕列表
    /// </summary>
    /// <param name="avid">稿件avID</param>
    /// <param name="cid">视频CID</param>
    /// <param name="segmentIndex">分包，每6分钟一包</param>
    /// <returns></returns>
    private static async Task<List<BiliDanmaku>> GetDanmakuProtoAsync(
        IBilibiliApiClient client,
        long avid,
        long cid,
        int segmentIndex,
        CancellationToken cancellationToken = default)
    {
        var url = $"https://api.bilibili.com/x/v2/dm/web/seg.so?type=1&oid={cid}&pid={avid}&segment_index={segmentIndex}";
        var danmakuList = new List<BiliDanmaku>();
        try
        {
            var request = new BilibiliHttpRequest(url, Referer);
            var input = await client.OpenReadAsync(request, cancellationToken).ConfigureAwait(false);
            await using (input.ConfigureAwait(false))
            {
                using var buffered = new MemoryStream();
                await input.CopyToAsync(buffered, cancellationToken).ConfigureAwait(false);
                buffered.Position = 0;
                var danmakus = DmSegMobileReply.Parser.ParseFrom(buffered);
                if (danmakus?.Elems == null)
                {
                    return danmakuList;
                }

                danmakuList.AddRange(danmakus.Elems.Select(dm => new BiliDanmaku
                {
                    Id = dm.Id,
                    Progress = dm.Progress,
                    Mode = dm.Mode,
                    Fontsize = dm.Fontsize,
                    Color = dm.Color,
                    MidHash = dm.MidHash,
                    Content = dm.Content,
                    Ctime = dm.Ctime,
                    Weight = dm.Weight,
                    Pool = dm.Pool
                }));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        return danmakuList;
    }

    /// <summary>
    /// 下载所有弹幕，返回弹幕列表
    /// </summary>
    /// <param name="avid">稿件avID</param>
    /// <param name="cid">视频CID</param>
    /// <returns></returns>
    public static async Task<IReadOnlyList<BiliDanmaku>> GetAllDanmakuProtoAsync(
        this IBilibiliApiClient client,
        long avid,
        long cid,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var danmakuList = new List<BiliDanmaku>();
        var segmentCount = await GetSegmentCountAsync(
            client,
            avid,
            cid,
            cancellationToken).ConfigureAwait(false);
        for (var segmentIndex = 1; segmentIndex <= segmentCount; segmentIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var danmakus = await GetDanmakuProtoAsync(
                client,
                avid,
                cid,
                segmentIndex,
                cancellationToken).ConfigureAwait(false);
            danmakuList.AddRange(danmakus);
        }

        return danmakuList;
    }
}
