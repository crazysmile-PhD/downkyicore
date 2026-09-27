using System;
using System.Collections.Generic;
using System.Linq;
using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Presentation;

namespace DownKyi.Services.Download;

internal sealed record DownloadMediaCapabilities(bool Video, bool Audio)
{
    public static DownloadMediaCapabilities From(PlayUrl? playUrl)
    {
        var hasCombinedMedia = playUrl?.Durl is { Count: > 0 };
        var dash = playUrl?.Dash;
        return new DownloadMediaCapabilities(
            hasCombinedMedia || dash?.Video is { Count: > 0 },
            hasCombinedMedia || DownloadAudioSelection.HasAnyAudio(dash));
    }
}

internal sealed record PreparedDownloadPage(
    VideoPage Page,
    DownloadMediaCapabilities AvailableMedia);

internal sealed record PreparedDownloadSection(
    VideoSection Section,
    IReadOnlyList<PreparedDownloadPage> Pages);

internal sealed record PreparedDownload(
    VideoInfoView Video,
    IReadOnlyList<PreparedDownloadSection> Sections)
{
    public static PreparedDownload Create(
        VideoInfoView video,
        IEnumerable<VideoSection> sections)
    {
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(sections);

        return new PreparedDownload(
            video,
            sections.Select(section =>
            {
                ArgumentNullException.ThrowIfNull(section);
                return new PreparedDownloadSection(
                    section,
                    section.VideoPages.Select(page =>
                    {
                        ArgumentNullException.ThrowIfNull(page);
                        return new PreparedDownloadPage(
                            page,
                            DownloadMediaCapabilities.From(page.PlayUrl));
                    }).ToArray());
            }).ToArray());
    }
}
