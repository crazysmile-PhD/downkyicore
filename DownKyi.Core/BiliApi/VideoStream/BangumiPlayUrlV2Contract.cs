using DownKyi.Core.BiliApi.VideoStream.Models;

namespace DownKyi.Core.BiliApi.VideoStream;

internal static class BangumiPlayUrlV2Contract
{
    public static PlayUrl SelectPayload(
        BangumiPlayUrlV2Origin response,
        string operationName)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        var result = BiliApiRequest.RequirePayload(
            response.Result,
            "result",
            operationName);
        var payload = BiliApiRequest.RequirePayload(
            result.VideoInfo,
            "result.video_info",
            operationName);
        if (payload.Durl.Count == 0
            && payload.Dash.Video.Count == 0
            && payload.Dash.Audio.Count == 0)
        {
            throw new BilibiliApiResponseException(
                operationName,
                $"{operationName} returned an empty 'result.video_info' playback payload.");
        }

        return payload;
    }
}
