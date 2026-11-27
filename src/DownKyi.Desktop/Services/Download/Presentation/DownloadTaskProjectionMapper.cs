using System;
using System.Linq;
using DownKyi.Application.Downloads;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Domain.Downloads;
using DownKyi.Models;
using DownKyi.Utils;
using DownKyi.ViewModels.DownloadManager;
using LegacyDownloadStatus = DownKyi.Models.DownloadStatus;

namespace DownKyi.Services.Download;

internal static class DownloadTaskProjectionMapper
{
    public static DownloadingItem ToDownloadingItem(DownloadTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var item = new DownloadingItem { Downloading = new Downloading() };
        Apply(task, item);
        return item;
    }

    public static DownloadedItem ToDownloadedItem(DownloadHistoryRecord history)
    {
        ArgumentNullException.ThrowIfNull(history);
        var downloadBase = ToDownloadBase(history);
        return new DownloadedItem
        {
            DownloadBase = downloadBase,
            HistoryRecord = history,
            Downloaded = new Downloaded
            {
                Id = history.Id.Value,
                MaxSpeedDisplay = history.MaximumSpeedText,
                FinishedTimestamp = history.FinishedTimestamp,
                FinishedTime = history.FinishedTimeText,
                DownloadBase = downloadBase
            }
        };
    }

    public static void Apply(DownloadTask task, DownloadingItem item, DownloadTask? preceding = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(item);
        if (preceding == null || preceding.Metadata != task.Metadata ||
            preceding.Plan.RequestedContent != task.Plan.RequestedContent ||
            preceding.Output.BasePath != task.Output.BasePath ||
            preceding.Output.FileSizeText != task.Output.FileSizeText)
        {
            item.DownloadBase = ToDownloadBase(task);
        }
        item.IsToggleVisible = !task.Transfer.HasLegacyProvenance
                               && (task.Phase != DownloadPhase.Failed
                                   || task.Failure?.IsTransient != false);
        item.Failure = task.Failure;
        var downloading = item.Downloading;
        downloading.Id = task.Id.Value;
        downloading.PlayStreamType = (PlayStreamType)task.Plan.StreamType;
        if (preceding == null || preceding.Transfer.MaximumBytesPerSecond != task.Transfer.MaximumBytesPerSecond)
        {
            downloading.MaxSpeed = task.Transfer.MaximumBytesPerSecond;
        }
        downloading.DownloadBase = item.DownloadBase;
        item.DownloadContent = task.Transfer.ActiveContent;
        item.DownloadStatusTitle = MapStatusText(task);
        if (preceding == null || preceding.Progress != task.Progress)
        {
            ApplyLiveProgress(task.Progress, item);
        }

        var status = MapPhase(task.Phase);
        if (preceding == null || downloading.DownloadStatus != status)
        {
            downloading.DownloadStatus = status;
            item.Downloading = downloading;
        }
    }

    public static void ApplyLiveProgress(DownloadProgress progress, DownloadingItem item, long maximumBytesPerSecond = 0)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(item);
        item.Progress = checked((float)progress.Percentage);
        item.DownloadingFileSize = progress.DownloadedSizeText;
        item.SpeedDisplay = progress.SpeedText;
        item.Downloading.MaxSpeed = Math.Max(
            item.Downloading.MaxSpeed,
            Math.Max(progress.BytesPerSecond, maximumBytesPerSecond));
    }

    private static DownloadBase ToDownloadBase(DownloadTask task)
    {
        return new DownloadBase
        {
            Id = task.Id.Value,
            NeedDownloadContent = task.Plan.RequestedContent,
            Bvid = task.Metadata.Media.Bvid,
            Avid = task.Metadata.Media.Avid,
            Cid = task.Metadata.Media.Cid,
            EpisodeId = task.Metadata.Media.EpisodeId,
            CoverUrl = task.Metadata.CoverAddress,
            PageCoverUrl = task.Metadata.PageCoverAddress,
            ZoneId = task.Metadata.ZoneId,
            Order = task.Metadata.Media.Order,
            MainTitle = task.Metadata.MainTitle,
            Name = task.Metadata.Name,
            Duration = task.Metadata.DurationText,
            VideoCodecName = task.Metadata.VideoCodecName,
            Resolution = new Quality
            {
                Id = task.Metadata.Resolution.Id,
                Name = task.Metadata.Resolution.Name
            },
            AudioCodec = new Quality
            {
                Id = task.Metadata.AudioCodec.Id,
                Name = task.Metadata.AudioCodec.Name
            },
            FilePath = task.Output.BasePath,
            FileSize = task.Output.FileSizeText,
            Page = task.Metadata.Media.Page
        };
    }

    private static DownloadBase ToDownloadBase(DownloadHistoryRecord history)
    {
        return new DownloadBase
        {
            Id = history.Id.Value,
            NeedDownloadContent = history.EffectiveRequestedContent,
            Cid = history.Cid,
            ZoneId = history.ZoneId,
            Order = history.Order,
            MainTitle = history.MainTitle,
            Name = history.Name,
            Duration = history.DurationText,
            VideoCodecName = history.VideoCodecName,
            Resolution = new Quality
            {
                Id = history.ResolutionId,
                Name = history.ResolutionName
            },
            AudioCodec = new Quality
            {
                Name = history.AudioCodecName
            },
            FileSize = history.FileSizeText
        };
    }

    private static LegacyDownloadStatus MapPhase(DownloadPhase phase)
    {
        return phase switch
        {
            DownloadPhase.Pausing => LegacyDownloadStatus.PauseStarted,
            DownloadPhase.Paused or DownloadPhase.Canceled => LegacyDownloadStatus.Pause,
            DownloadPhase.Downloading => LegacyDownloadStatus.Downloading,
            DownloadPhase.Failed => LegacyDownloadStatus.DownloadFailed,
            DownloadPhase.Completed => LegacyDownloadStatus.DownloadSucceed,
            _ => LegacyDownloadStatus.WaitForDownload
        };
    }

    private static string? MapStatusText(DownloadTask task)
    {
        return task.Phase switch
        {
            DownloadPhase.Queued => DictionaryResource.GetString("Waiting"),
            DownloadPhase.Pausing => DictionaryResource.GetString("Pausing"),
            DownloadPhase.Paused => DictionaryResource.GetString("Paused"),
            DownloadPhase.Failed => task.Failure?.Message
                ?? DictionaryResource.GetString("DownloadFailed"),
            DownloadPhase.Downloading when string.IsNullOrWhiteSpace(task.Transfer.StatusText) =>
                DictionaryResource.GetString("WhileDownloading"),
            _ => task.Transfer.StatusText
        };
    }
}
