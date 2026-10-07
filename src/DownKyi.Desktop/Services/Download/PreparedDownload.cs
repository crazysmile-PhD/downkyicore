using System;
using System.Collections.Generic;
using System.Linq;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Domain.Downloads;
using DownKyi.Presentation;

namespace DownKyi.Services.Download;

[Flags]
internal enum DownloadMediaOutputModes
{
    None = 0,
    VideoOnly = 1,
    AudioOnly = 2,
    AudioVideo = 4
}

internal sealed record DownloadMediaCapabilities(DownloadMediaOutputModes SupportedModes)
{
    public bool HasAnyMedia => SupportedModes != DownloadMediaOutputModes.None;

    public bool Supports(DownloadContentSelection requestedContent)
    {
        ArgumentNullException.ThrowIfNull(requestedContent);
        var requestedMode = GetRequestedMode(requestedContent);
        return requestedMode == DownloadMediaOutputModes.None
               || SupportedModes.HasFlag(requestedMode);
    }

    public bool TryGetCompatibleContent(
        DownloadContentSelection requestedContent,
        out DownloadContentSelection compatibleContent)
    {
        ArgumentNullException.ThrowIfNull(requestedContent);
        if (Supports(requestedContent))
        {
            compatibleContent = requestedContent;
            return true;
        }

        if (requestedContent.Audio && requestedContent.Video)
        {
            if (SupportedModes.HasFlag(DownloadMediaOutputModes.VideoOnly))
            {
                compatibleContent = requestedContent with { Audio = false };
                return true;
            }

            if (SupportedModes.HasFlag(DownloadMediaOutputModes.AudioOnly))
            {
                compatibleContent = requestedContent with { Video = false };
                return true;
            }
        }

        compatibleContent = requestedContent with { Audio = false, Video = false };
        return false;
    }

    public static DownloadMediaCapabilities From(
        PlayUrlAvailability? availability,
        VideoQuality? selectedVideoQuality,
        string selectedAudioQuality)
    {
        if (availability == null || selectedVideoQuality == null)
        {
            return new DownloadMediaCapabilities(DownloadMediaOutputModes.None);
        }

        var streamKind = selectedVideoQuality.IsDurl
            ? PlayUrlStreamKind.Durl
            : PlayUrlStreamKind.Dash;
        var selectedCodecId = PlaybackQualityCatalog.GetCodecIds()
            .FirstOrDefault(codec => string.Equals(
                codec.Name,
                selectedVideoQuality.SelectedVideoCodec,
                StringComparison.Ordinal))
            ?.Id;
        if (selectedCodecId == null)
        {
            return new DownloadMediaCapabilities(DownloadMediaOutputModes.None);
        }

        var hasVideo = availability.Video.Any(video =>
            video.StreamKind == streamKind
            && video.Quality == selectedVideoQuality.Quality
            && video.CodecId == selectedCodecId.Value);
        if (!hasVideo)
        {
            return new DownloadMediaCapabilities(DownloadMediaOutputModes.None);
        }

        if (streamKind == PlayUrlStreamKind.Durl)
        {
            return new DownloadMediaCapabilities(
                DownloadMediaOutputModes.VideoOnly | DownloadMediaOutputModes.AudioVideo);
        }

        var selectedAudioId = PlaybackQualityCatalog.GetAudioQualities()
            .FirstOrDefault(audio => string.Equals(
                audio.Name,
                selectedAudioQuality,
                StringComparison.Ordinal))
            ?.Id;
        var modes = DownloadMediaOutputModes.VideoOnly;
        if (selectedAudioId is > 0 && availability.Audio.Contains(selectedAudioId.Value))
        {
            modes |= DownloadMediaOutputModes.AudioOnly | DownloadMediaOutputModes.AudioVideo;
        }

        return new DownloadMediaCapabilities(modes);
    }

    private static DownloadMediaOutputModes GetRequestedMode(
        DownloadContentSelection requestedContent)
    {
        return (requestedContent.Audio, requestedContent.Video) switch
        {
            (true, true) => DownloadMediaOutputModes.AudioVideo,
            (true, false) => DownloadMediaOutputModes.AudioOnly,
            (false, true) => DownloadMediaOutputModes.VideoOnly,
            _ => DownloadMediaOutputModes.None
        };
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
                            DownloadMediaCapabilities.From(
                                page.PlaybackAvailability,
                                page.VideoQuality,
                                page.AudioQualityFormat));
                    }).ToArray());
            }).ToArray());
    }
}

internal sealed record FinalizedDownloadPage(
    VideoPage Page,
    VideoQuality VideoQuality,
    DownloadContentSelection RequestedContent);

internal sealed record FinalizedDownloadSection(
    VideoSection Section,
    IReadOnlyList<FinalizedDownloadPage> Pages);

internal sealed record FinalizedDownload(
    VideoInfoView Video,
    IReadOnlyList<FinalizedDownloadSection> Sections);
