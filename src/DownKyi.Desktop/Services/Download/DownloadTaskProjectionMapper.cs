using System;
using System.Linq;
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
    public static DownloadTask CreateNewTask(DownloadingItem item, DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(item);
        var downloadBase = item.DownloadBase
            ?? throw new ArgumentException("A new download requires base metadata.", nameof(item));
        var downloading = item.Downloading
            ?? throw new ArgumentException("A new download requires runtime data.", nameof(item));
        if (downloading.DownloadStatus is not (
                LegacyDownloadStatus.NotStarted or LegacyDownloadStatus.WaitForDownload))
        {
            throw new ArgumentException(
                "New downloads must enter through the queued Domain state.",
                nameof(item));
        }

        return DownloadTask.Create(
            new DownloadTaskId(downloadBase.Id),
            ToMetadata(downloadBase),
            new DownloadPlan(
                downloadBase.NeedDownloadContent,
                downloading.DownloadFiles,
                (int)downloading.PlayStreamType),
            new DownloadOutput(downloadBase.FilePath, downloadBase.FileSize),
            createdAtUtc);
    }

    public static DownloadingItem ToDownloadingItem(DownloadTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var item = new DownloadingItem();
        Apply(task, item);
        return item;
    }

    public static DownloadedItem ToDownloadedItem(DownloadTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var completion = task.Completion
            ?? throw new InvalidOperationException("Completed download is missing completion details.");
        var downloadBase = ToDownloadBase(task);
        return new DownloadedItem
        {
            DownloadBase = downloadBase,
            Downloaded = new Downloaded
            {
                Id = task.Id.Value,
                MaxSpeedDisplay = completion.MaximumSpeedText,
                FinishedTimestamp = completion.FinishedTimestamp,
                FinishedTime = completion.FinishedTimeText,
                DownloadBase = downloadBase
            }
        };
    }

    public static void Apply(DownloadTask task, DownloadingItem item)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(item);
        var downloadBase = ToDownloadBase(task);
        item.DownloadBase = downloadBase;
        item.Downloading = new Downloading
        {
            Id = task.Id.Value,
            Gid = task.Transfer.BackendIdentity,
            DownloadFiles = task.Plan.TransferFiles.ToDictionary(entry => entry.Key, entry => entry.Value),
            DownloadedFiles = task.Transfer.CompletedFileKeys.ToList(),
            PlayStreamType = (PlayStreamType)task.Plan.StreamType,
            DownloadStatus = MapPhase(task.Phase),
            DownloadContent = task.Transfer.ActiveContent,
            DownloadStatusTitle = MapStatusText(task),
            Progress = checked((float)task.Progress.Percentage),
            DownloadingFileSize = task.Progress.DownloadedSizeText,
            MaxSpeed = task.Transfer.MaximumBytesPerSecond,
            SpeedDisplay = task.Progress.SpeedText,
            DownloadBase = downloadBase
        };
    }

    public static void ApplyLiveProgress(DownloadProgress progress, DownloadingItem item)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(item);
        item.Progress = checked((float)progress.Percentage);
        item.DownloadingFileSize = progress.DownloadedSizeText;
        item.SpeedDisplay = progress.SpeedText;
        item.Downloading.MaxSpeed = Math.Max(
            item.Downloading.MaxSpeed,
            progress.BytesPerSecond);
    }

    private static DownloadTaskMetadata ToMetadata(DownloadBase downloadBase)
    {
        return new DownloadTaskMetadata(
            new DownloadMediaIdentity(
                downloadBase.Bvid,
                downloadBase.Avid,
                downloadBase.Cid,
                downloadBase.EpisodeId,
                downloadBase.Page,
                downloadBase.Order),
            downloadBase.MainTitle,
            downloadBase.Name,
            downloadBase.Duration,
            downloadBase.VideoCodecName,
            new DownloadQuality(downloadBase.Resolution.Id, downloadBase.Resolution.Name),
            new DownloadQuality(downloadBase.AudioCodec.Id, downloadBase.AudioCodec.Name),
            downloadBase.CoverUrl,
            downloadBase.PageCoverUrl,
            downloadBase.ZoneId);
    }

    private static DownloadBase ToDownloadBase(DownloadTask task)
    {
        return new DownloadBase
        {
            Id = task.Id.Value,
            NeedDownloadContent = task.Plan.RequestedAssets.ToDictionary(entry => entry.Key, entry => entry.Value),
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
            DownloadPhase.Pausing or DownloadPhase.Paused => DictionaryResource.GetString("Pausing"),
            DownloadPhase.Failed => task.Failure?.Message
                ?? DictionaryResource.GetString("DownloadFailed"),
            DownloadPhase.Downloading when string.IsNullOrWhiteSpace(task.Transfer.StatusText) =>
                DictionaryResource.GetString("WhileDownloading"),
            _ => task.Transfer.StatusText
        };
    }
}
