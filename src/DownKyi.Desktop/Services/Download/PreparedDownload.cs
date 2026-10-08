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

internal sealed record DownloadMediaCapabilities(
    DownloadMediaOutputModes SupportedModes,
    string? LowerVideoQuality = null,
    string? LowerAudioQuality = null)
{
    public bool HasAnyMedia => SupportedModes != DownloadMediaOutputModes.None;

    public bool UsesLowerQuality => LowerVideoQuality != null || LowerAudioQuality != null;

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
        string selectedAudioQuality,
        int? preferredVideoQuality = null,
        int? preferredAudioQuality = null)
    {
        if (availability == null)
        {
            return new DownloadMediaCapabilities(DownloadMediaOutputModes.None);
        }

        var audioQualities = PlaybackQualityCatalog.GetAudioQualities();
        var selectedAudio = audioQualities
            .FirstOrDefault(audio => string.Equals(
                audio.Name,
                selectedAudioQuality,
                StringComparison.Ordinal));
        var hasSelectedAudio = selectedAudio?.Id is { } selectedAudioId
            && availability.Audio.Contains(selectedAudioId);
        var selectedVideoKind = ResolveSelectedVideoKind(
            availability,
            selectedVideoQuality);
        return new DownloadMediaCapabilities(
            GetSupportedModes(selectedVideoKind, hasSelectedAudio),
            LowerVideoQuality: selectedVideoKind != null
                               && preferredVideoQuality is { } videoPreference
                               && selectedVideoQuality!.Quality < videoPreference
                ? selectedVideoQuality.QualityFormat
                : null,
            LowerAudioQuality: hasSelectedAudio
                               && preferredAudioQuality is { } audioPreference
                               && IsLowerAudioQuality(audioQualities, selectedAudio!.Id, audioPreference)
                ? selectedAudio.Name
                : null);
    }

    private static bool IsLowerAudioQuality(
        IReadOnlyList<Quality> catalog,
        int selectedId,
        int preferredId)
    {
        var normalizedPreference = preferredId >= 31000 ? preferredId - 1000 : preferredId;
        var selectedRank = Array.FindIndex(catalog.ToArray(), quality => quality.Id == selectedId);
        var preferredRank = Array.FindIndex(catalog.ToArray(), quality => quality.Id == normalizedPreference);
        return selectedRank >= 0 && preferredRank >= 0 && selectedRank < preferredRank;
    }

    private static PlayUrlStreamKind? ResolveSelectedVideoKind(
        PlayUrlAvailability availability,
        VideoQuality? selectedVideoQuality)
    {
        if (selectedVideoQuality == null)
        {
            return null;
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
        return selectedCodecId is { } codecId
               && availability.Video.Any(video =>
                   video.StreamKind == streamKind
                   && video.Quality == selectedVideoQuality.Quality
                   && video.CodecId == codecId)
            ? streamKind
            : null;
    }

    private static DownloadMediaOutputModes GetSupportedModes(
        PlayUrlStreamKind? selectedVideoKind,
        bool hasSelectedAudio)
    {
        return (selectedVideoKind, hasSelectedAudio) switch
        {
            (null, false) => DownloadMediaOutputModes.None,
            (null, true) => DownloadMediaOutputModes.AudioOnly,
            (PlayUrlStreamKind.Dash, false) => DownloadMediaOutputModes.VideoOnly,
            (PlayUrlStreamKind.Dash, true) =>
                DownloadMediaOutputModes.VideoOnly |
                DownloadMediaOutputModes.AudioOnly |
                DownloadMediaOutputModes.AudioVideo,
            // DURL is a combined stream: it satisfies video-only and audio+video,
            // but a separate DASH audio stream is still required for audio-only.
            (PlayUrlStreamKind.Durl, false) =>
                DownloadMediaOutputModes.VideoOnly |
                DownloadMediaOutputModes.AudioVideo,
            (PlayUrlStreamKind.Durl, true) =>
                DownloadMediaOutputModes.VideoOnly |
                DownloadMediaOutputModes.AudioOnly |
                DownloadMediaOutputModes.AudioVideo,
            _ => DownloadMediaOutputModes.None
        };
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
        IEnumerable<VideoSection> sections,
        int? preferredVideoQuality = null,
        int? preferredAudioQuality = null)
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
                                page.AudioQualityFormat,
                                preferredVideoQuality,
                                preferredAudioQuality));
                    }).ToArray());
            }).ToArray());
    }
}

internal sealed record FinalizedDownloadPage(
    VideoPage Page,
    VideoQuality? VideoQuality,
    DownloadContentSelection RequestedContent);

internal sealed record FinalizedDownloadSection(
    VideoSection Section,
    IReadOnlyList<FinalizedDownloadPage> Pages);

internal sealed record FinalizedDownload(
    VideoInfoView Video,
    IReadOnlyList<FinalizedDownloadSection> Sections);
