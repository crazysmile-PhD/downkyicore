using System;
using System.Collections.Generic;
using System.Globalization;
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
    bool VideoSelectionRequired = false)
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

        if (requestedContent.Video && VideoSelectionRequired)
        {
            compatibleContent = requestedContent with { Audio = false, Video = false };
            return false;
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
        if (availability == null)
        {
            return new DownloadMediaCapabilities(DownloadMediaOutputModes.None);
        }

        var selectedAudioId = PlaybackQualityCatalog.GetAudioQualities()
            .FirstOrDefault(audio => string.Equals(
                audio.Name,
                selectedAudioQuality,
                StringComparison.Ordinal))
            ?.Id;
        var hasAudio = selectedAudioId is > 0
            && availability.Audio.Contains(selectedAudioId.Value);
        var videoKind = ResolveSelectedVideoKind(availability, selectedVideoQuality);
        var modes = videoKind switch
        {
            PlayUrlStreamKind.Durl =>
                DownloadMediaOutputModes.VideoOnly | DownloadMediaOutputModes.AudioVideo,
            PlayUrlStreamKind.Dash => DownloadMediaOutputModes.VideoOnly,
            _ => DownloadMediaOutputModes.None
        };
        if (hasAudio)
        {
            modes |= DownloadMediaOutputModes.AudioOnly;
            if (videoKind == PlayUrlStreamKind.Dash)
            {
                modes |= DownloadMediaOutputModes.AudioVideo;
            }
        }

        return new DownloadMediaCapabilities(
            modes,
            VideoSelectionRequired: selectedVideoQuality == null && availability.Video.Count > 0);
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

internal sealed record DownloadQualitySubstitution(
    int RequestedQuality,
    string RequestedName,
    int SelectedQuality,
    string SelectedName);

internal sealed record DownloadQualitySubstitutions(
    DownloadQualitySubstitution? Video,
    DownloadQualitySubstitution? Audio)
{
    public static DownloadQualitySubstitutions None { get; } = new(
        Video: null,
        Audio: null);

    public bool HasAny => Video != null || Audio != null;

    public DownloadQualitySubstitutions For(DownloadContentSelection content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new DownloadQualitySubstitutions(
            content.Video ? Video : null,
            content.Audio ? Audio : null);
    }

    public static DownloadQualitySubstitutions From(VideoPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return new DownloadQualitySubstitutions(
            CreateVideoSubstitution(page.VideoQualityMatch, page.VideoQuality),
            CreateAudioSubstitution(page.AudioQualityMatch, page.AudioQualityFormat));
    }

    private static DownloadQualitySubstitution? CreateVideoSubstitution(
        PlaybackQualityMatch? match,
        VideoQuality? selected)
    {
        return (match, selected) switch
        {
            ({ RequiresLowerQualityConfirmation: true, SelectedQuality: { } selectedQuality },
                { } selectedVideo) => new DownloadQualitySubstitution(
                    match.RequestedQuality,
                    ResolveName(PlaybackQualityCatalog.GetResolutions(), match.RequestedQuality),
                    selectedQuality,
                    ResolveName(
                        PlaybackQualityCatalog.GetResolutions(),
                        selectedQuality,
                        selectedVideo.QualityFormat)),
            _ => null
        };
    }

    private static DownloadQualitySubstitution? CreateAudioSubstitution(
        PlaybackQualityMatch? match,
        string selectedName)
    {
        return match switch
        {
            { RequiresLowerQualityConfirmation: true, SelectedQuality: { } selectedQuality } =>
                new DownloadQualitySubstitution(
                    match.RequestedQuality,
                    ResolveName(PlaybackQualityCatalog.GetAudioQualities(), match.RequestedQuality),
                    selectedQuality,
                    ResolveName(
                        PlaybackQualityCatalog.GetAudioQualities(),
                        selectedQuality,
                        selectedName)),
            _ => null
        };
    }

    private static string ResolveName(
        IEnumerable<Quality> catalog,
        int quality,
        string? preferredName = null) => new[]
        {
            preferredName?.Trim(),
            catalog.FirstOrDefault(option => option.Id == quality)?.Name,
            quality.ToString(CultureInfo.InvariantCulture)
        }
        .OfType<string>()
        .First(name => name.Length > 0);
}

internal sealed record PreparedDownloadPage(
    VideoPage Page,
    DownloadMediaCapabilities AvailableMedia,
    DownloadQualitySubstitutions QualitySubstitutions);

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
                                page.AudioQualityFormat),
                            DownloadQualitySubstitutions.From(page));
                    }).ToArray());
            }).ToArray());
    }
}

internal sealed record FinalizedDownloadPage(
    VideoPage Page,
    VideoQuality? VideoQuality,
    DownloadContentSelection RequestedContent,
    DownloadContentSelection FinalizedContent);

internal sealed record FinalizedDownloadSection(
    VideoSection Section,
    IReadOnlyList<FinalizedDownloadPage> Pages);

internal sealed record FinalizedDownload(
    VideoInfoView Video,
    IReadOnlyList<FinalizedDownloadSection> Sections,
    DownloadPlanningStopReason? StopReason,
    int CandidateCount,
    int SkippedCount);
