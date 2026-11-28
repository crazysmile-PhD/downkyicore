using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DownKyi.Application.FileName;
using DownKyi.Application.Settings;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.BiliApi.Zone;
using DownKyi.Core.FileName;
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
                selectedVideo,
                audioQualityFormat,
                settings),
            Order = page.Order,
            MainTitle = video.Title,
            Name = page.Name,
            Duration = page.Duration,
            VideoCodecName = selectedVideo?.SelectedVideoCodec ?? string.Empty,
            Resolution = new Quality
            {
                Name = selectedVideo?.QualityFormat ?? string.Empty,
                Id = selectedVideo?.Quality ?? 0
            },
            AudioCodec = content.Audio ? audioCodec : new Quality(),
            Page = page.Page
        };
        downloadBase.NeedDownloadContent = content with
        {
            MediaKind = mediaKind,
            DanmakuOutputFormat = content.Danmaku
                ? ToDomainDanmakuOutputFormat(settings.Danmaku.OutputFormat)
                : null
        };

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
        var path = new StringBuilder();
        foreach (var part in settings.Video.FileNameParts)
        {
            path.Append((int)part >= 100 ? HyphenSeparated.Hyphen[(int)part] : part switch
            {
                FileNamePart.Order => settings.Video.OrderFormat switch
                {
                    OrderFormat.Natural => page.Order.ToString(CultureInfo.InvariantCulture),
                    OrderFormat.LeadingZeros => page.Order.ToString(
                        "D" + Math.Abs(section.VideoPages.Count).ToString(CultureInfo.InvariantCulture).Length,
                        CultureInfo.InvariantCulture),
                    // Preserve the legacy placeholder when no order format is selected.
                    _ => "ORDER"
                },
                FileNamePart.Section => Format.FormatFileName(sectionCount > 1 ? section.Title : string.Empty),
                FileNamePart.MainTitle => Format.FormatFileName(video.Title),
                FileNamePart.PageTitle => Format.FormatFileName(page.Name),
                FileNamePart.VideoZone => video.VideoZone.Split('>')[0],
                FileNamePart.AudioQuality => audioQualityFormat,
                FileNamePart.VideoQuality => videoQuality?.QualityFormat ?? string.Empty,
                FileNamePart.VideoCodec => GetCodecLabel(videoQuality?.SelectedVideoCodec ?? string.Empty),
                FileNamePart.VideoPublishTime => page.PublishTime,
                FileNamePart.Avid => string.Create(CultureInfo.InvariantCulture, $"av{page.Avid}"),
                FileNamePart.Bvid => page.Bvid,
                FileNamePart.Cid => page.Cid.ToString(CultureInfo.InvariantCulture),
                FileNamePart.UpMid => (page.Owner?.Mid ?? -1).ToString(CultureInfo.InvariantCulture),
                FileNamePart.UpName => Format.FormatFileName(page.Owner?.Name ?? string.Empty),
                _ => string.Empty
            });
        }

        var relativePath = Regex.Replace(path.ToString(), @"//+", "/").TrimEnd('/').TrimStart('/');
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
