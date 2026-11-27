using System;
using System.Globalization;
using System.IO;
using System.Linq;
using DownKyi.Application.Settings;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.BiliApi.Zone;
using DownKyi.Core.FileName;
using DownKyi.Core.Utils;
using DownKyi.Domain.Downloads;
using DownKyi.Presentation;
using DownKyi.Utils;

namespace DownKyi.Services.Download;

internal static class DownloadTaskDraftFactory
{
    public static DownloadTaskDraft Create(
        string directory,
        VideoInfoView video,
        VideoSection section,
        int sectionCount,
        VideoPage page,
        VideoQuality? videoQuality,
        string audioQualityFormat,
        DownloadMediaCapabilities availableMedia,
        ApplicationSettings settings,
        DownloadContentSelection content)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(content);
        if (!content.HasAnyRequestedAction)
        {
            throw new ArgumentException(
                "A download task must contain at least one finalized action.",
                nameof(content));
        }

        ArgumentNullException.ThrowIfNull(audioQualityFormat);
        ArgumentNullException.ThrowIfNull(availableMedia);
        if (!availableMedia.Supports(content))
        {
            throw new InvalidOperationException(
                "A media download draft requires a supported finalized playback selection.");
        }

        if (content.Video && videoQuality == null)
        {
            throw new InvalidOperationException(
                "A video download draft requires a finalized video quality.");
        }

        var mediaKind = content.Video
            ? videoQuality!.IsDurl ? DownloadMediaKind.Durl : DownloadMediaKind.Dash
            : content.Audio ? DownloadMediaKind.Dash : DownloadMediaKind.None;
        var selectedVideo = content.Video ? videoQuality : null;
        var audioCodec = PlaybackQualityCatalog.GetAudioQualities()
            .FirstOrDefault(quality => quality.Name == audioQualityFormat) ?? new Quality();
        var metadata = new DownloadTaskMetadata(
            new DownloadMediaIdentity(page.Bvid, page.Avid, page.Cid, page.EpisodeId, page.Page, page.Order),
            video.Title,
            page.Name,
            page.Duration,
            selectedVideo?.SelectedVideoCodec ?? string.Empty,
            new DownloadQuality(selectedVideo?.Quality ?? 0, selectedVideo?.QualityFormat ?? string.Empty),
            new DownloadQuality(content.Audio ? audioCodec.Id : 0, content.Audio ? audioCodec.Name : string.Empty),
            video.CoverUrl,
            page.FirstFrame,
            ResolveZoneId(video.TypeId));
        return new DownloadTaskDraft(
            new DownloadTaskId(Guid.NewGuid().ToString("N")),
            metadata,
            content with
            {
                MediaKind = mediaKind,
                DanmakuOutputFormat = content.Danmaku
                    ? ToDomainDanmakuOutputFormat(settings.Danmaku.OutputFormat)
                    : null
            },
            (int)ResolvePlayStreamType(video.TypeId),
            BuildFilePath(
                directory,
                video,
                section,
                sectionCount,
                page,
                selectedVideo,
                audioQualityFormat,
                settings));
    }

    private static int ResolveZoneId(int typeId)
    {
        var zoneList = VideoZone.Current.Zones;
        var zone = zoneList.FirstOrDefault(item => item.Id == typeId);
        if (zone == null)
        {
            return -1;
        }

        if (zone.ParentId == 0)
        {
            return zone.Id;
        }

        return zoneList.FirstOrDefault(item => item.Id == zone.ParentId)?.Id ?? -1;
    }

    private static string BuildFilePath(
        string directory,
        VideoInfoView video,
        VideoSection section,
        int sectionCount,
        VideoPage page,
        VideoQuality? videoQuality,
        string audioQualityFormat,
        ApplicationSettings settings)
    {
        var sectionName = sectionCount > 1 ? section.Title : string.Empty;
        var fileName = FileNameBuilder.Create(settings.Video.FileNameParts)
            .SetSection(Format.FormatFileName(sectionName))
            .SetMainTitle(Format.FormatFileName(video.Title))
            .SetPageTitle(Format.FormatFileName(page.Name))
            .SetVideoZone(video.VideoZone.Split('>')[0])
            .SetAudioQuality(audioQualityFormat)
            .SetVideoQuality(videoQuality?.QualityFormat ?? string.Empty)
            .SetVideoCodec(GetCodecLabel(videoQuality?.SelectedVideoCodec ?? string.Empty))
            .SetVideoPublishTime(page.PublishTime)
            .SetAvid(page.Avid)
            .SetBvid(page.Bvid)
            .SetCid(page.Cid)
            .SetUpMid(page.Owner?.Mid ?? -1)
            .SetUpName(Format.FormatFileName(page.Owner?.Name ?? string.Empty));

        switch (settings.Video.OrderFormat)
        {
            case OrderFormat.Natural:
                fileName.SetOrder(page.Order);
                break;
            case OrderFormat.LeadingZeros:
                fileName.SetOrder(page.Order, section.VideoPages.Count);
                break;
        }

        var relativePath = fileName.RelativePath();
        if (!relativePath.Any(char.IsLetterOrDigit))
        {
            relativePath = page.Cid.ToString(CultureInfo.InvariantCulture);
        }

        return Path.Combine(directory, relativePath);
    }

    private static string GetCodecLabel(string codec)
    {
        if (codec.Contains("AVC", StringComparison.Ordinal))
        {
            return "AVC";
        }

        if (codec.Contains("HEVC", StringComparison.Ordinal))
        {
            return "HEVC";
        }

        if (codec.Contains("Dolby", StringComparison.Ordinal))
        {
            return "Dolby Vision";
        }

        return codec.Contains("AV1", StringComparison.Ordinal) ? "AV1" : string.Empty;
    }

    private static PlayStreamType ResolvePlayStreamType(int typeId)
    {
        return typeId switch
        {
            -10 => PlayStreamType.Cheese,
            13 or 23 or 177 or 167 or 11 => PlayStreamType.Bangumi,
            _ => PlayStreamType.Video
        };
    }

    private static DownloadDanmakuOutputFormat ToDomainDanmakuOutputFormat(
        DanmakuOutputFormat format) => format switch
        {
            DanmakuOutputFormat.Ass => DownloadDanmakuOutputFormat.Ass,
            DanmakuOutputFormat.Xml => DownloadDanmakuOutputFormat.Xml,
            DanmakuOutputFormat.AssAndXml =>
                DownloadDanmakuOutputFormat.Ass | DownloadDanmakuOutputFormat.Xml,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
        };
}
