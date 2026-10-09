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
        if (string.Equals(
                result.PlayCheck?.PlayDetail,
                "PLAY_PREVIEW",
                StringComparison.Ordinal)
            || string.Equals(
                result.PlayVideoType,
                "preview",
                StringComparison.OrdinalIgnoreCase)
            || payload.IsPreview == true)
        {
            throw new BilibiliApiResponseException(
                operationName,
                $"{operationName} returned preview-only playback content.");
        }

        if (!PlayUrlAvailability.From(payload).HasPlayableMedia)
        {
            throw new PlaybackResourceUnavailableException(operationName);
        }

        return payload;
    }

}
