using System.Globalization;
using DownKyi.Core.BiliApi.VideoStream.Models;

namespace DownKyi.Core.BiliApi.VideoStream;

public sealed class PlaybackSelectionUnavailableException : InvalidOperationException
{
    public PlaybackSelectionUnavailableException()
        : this(0, null, null, null)
    {
    }

    public PlaybackSelectionUnavailableException(string message)
        : base(message)
    {
    }

    public PlaybackSelectionUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public PlaybackSelectionUnavailableException(
        int quality,
        int? videoCodecId,
        int? audioId,
        PlayUrlStreamKind? streamKind)
        : base(CreateMessage(quality, videoCodecId, audioId, streamKind))
    {
        Quality = quality;
        VideoCodecId = videoCodecId;
        AudioId = audioId;
        StreamKind = streamKind;
    }

    public int Quality { get; }

    public int? VideoCodecId { get; }

    public int? AudioId { get; }

    public PlayUrlStreamKind? StreamKind { get; }

    private static string CreateMessage(
        int quality,
        int? videoCodecId,
        int? audioId,
        PlayUrlStreamKind? streamKind)
    {
        return "The requested Bangumi playback selection is unavailable (" +
               $"quality={quality.ToString(CultureInfo.InvariantCulture)}," +
               $"codec={videoCodecId?.ToString(CultureInfo.InvariantCulture) ?? "any"}," +
               $"audio={audioId?.ToString(CultureInfo.InvariantCulture) ?? "none"}," +
               $"kind={streamKind?.ToString() ?? "any"}).";
    }
}
