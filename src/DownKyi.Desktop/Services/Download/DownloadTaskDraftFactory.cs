using System;
using System.IO;
using System.Linq;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.BiliApi.Zone;
using DownKyi.Core.FileName;
using DownKyi.Core.Settings;
using DownKyi.Core.Utils;
using DownKyi.Domain.Downloads;
using DownKyi.Models;
using DownKyi.Presentation;
using DownKyi.Utils;
using DownKyi.ViewModels.DownloadManager;

namespace DownKyi.Services.Download;

internal static class DownloadTaskDraftFactory
{
    public static DownloadingItem Create(
        string directory,
        VideoInfoView video,
        VideoSection section,
        int sectionCount,
        VideoPage page,
        VideoQuality? videoQuality,
        ApplicationSettings settings,
        DownloadContentSelection content)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(content);

        var mediaKind = ResolveMediaKind(content, videoQuality);

        var audioCodec = PlaybackQualityCatalog.GetAudioQualities()
            .FirstOrDefault(quality => quality.Name == page.AudioQualityFormat) ?? new Quality();
        var downloadBase = new DownloadBase
        {
            Bvid = page.Bvid,
            Avid = page.Avid,
            Cid = page.Cid,
            EpisodeId = page.EpisodeId,
            CoverUrl = video.CoverUrl,
            PageCoverUrl = page.FirstFrame,
            ZoneId = ResolveZoneId(video.TypeId),
            FilePath = BuildFilePath(
                directory,
                video,
                section,
                sectionCount,
                page,
                videoQuality,
                settings),
            Order = page.Order,
            MainTitle = video.Title,
            Name = page.Name,
            Duration = page.Duration,
            VideoCodecName = videoQuality?.SelectedVideoCodec ?? string.Empty,
            Resolution = new Quality
            {
                Name = videoQuality?.QualityFormat ?? string.Empty,
                Id = videoQuality?.Quality ?? settings.Video.Quality
            },
            AudioCodec = audioCodec,
            Page = page.Page
        };
        downloadBase.NeedDownloadContent = content with { MediaKind = mediaKind };

        return new DownloadingItem
        {
            DownloadBase = downloadBase,
            Downloading = new Downloading
            {
                PlayStreamType = ResolvePlayStreamType(video.TypeId),
                DownloadStatus = DownloadStatus.NotStarted
            }
        };
    }

    private static DownloadMediaKind ResolveMediaKind(
        DownloadContentSelection content,
        VideoQuality? videoQuality)
    {
        if (!content.Audio && !content.Video)
        {
            return DownloadMediaKind.None;
        }

        if (!content.Video)
        {
            if (videoQuality?.IsDurl == true)
            {
                throw new InvalidOperationException(
                    "Audio-only DURL downloads are not supported.");
            }

            return DownloadMediaKind.Dash;
        }

        if (videoQuality == null)
        {
            throw new InvalidOperationException(
                "A video download draft requires a finalized video quality.");
        }

        return videoQuality.IsDurl
            ? DownloadMediaKind.Durl
            : DownloadMediaKind.Dash;
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
        ApplicationSettings settings)
    {
        var sectionName = sectionCount > 1 ? section.Title : string.Empty;
        var fileName = FileNameBuilder.Create(settings.Video.FileNameParts)
            .SetSection(Format.FormatFileName(sectionName))
            .SetMainTitle(Format.FormatFileName(video.Title))
            .SetPageTitle(Format.FormatFileName(page.Name))
            .SetVideoZone(video.VideoZone.Split('>')[0])
            .SetAudioQuality(page.AudioQualityFormat)
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

        var filePath = Path.Combine(directory, fileName.RelativePath());
        return filePath;
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
}
