using DownKyi.Core.Settings;

namespace DownKyi.Services.Download;

internal sealed record DownloadContentSelection(
    bool Audio,
    bool Video,
    bool Danmaku,
    bool Subtitle,
    bool Cover)
{
    public static DownloadContentSelection All { get; } = new(
        Audio: true,
        Video: true,
        Danmaku: true,
        Subtitle: true,
        Cover: true);

    public static DownloadContentSelection From(VideoContentApplicationSettings settings)
    {
        return new DownloadContentSelection(
            settings.DownloadAudio,
            settings.DownloadVideo,
            settings.DownloadDanmaku,
            settings.DownloadSubtitle,
            settings.DownloadCover);
    }
}
