using System;
using System.Collections.Generic;
using System.Formats.Nrbf;
using System.Linq;
using DownKyi.Domain.Downloads;
using DownKyi.Models;

namespace DownKyi.Services.Migration;

internal static class LegacyDownloadTaskMapper
{
    public static Dictionary<string, bool> ReadRequestedAssets(ClassRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var values = record
            .GetArrayRecord("KeyValuePairs")?
            .GetArray(typeof(KeyValuePair<string, bool>[]));
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var item in values?.Cast<ClassRecord>() ?? [])
        {
            result[item.GetString("key") ?? string.Empty] = item.GetBoolean("value");
        }

        return result;
    }

    public static DownloadTask RestoreCompleted(Downloaded downloaded, DateTimeOffset migratedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(downloaded);
        var downloadBase = downloaded.DownloadBase
            ?? throw new ArgumentException("Legacy history is missing its base record.", nameof(downloaded));
        var finishedAtUtc = downloaded.FinishedTimestamp > 0
            ? DateTimeOffset.FromUnixTimeSeconds(downloaded.FinishedTimestamp)
            : migratedAtUtc;
        var createdAtUtc = finishedAtUtc <= migratedAtUtc ? finishedAtUtc : migratedAtUtc;

        return DownloadTask.Restore(
            new DownloadTaskId(downloadBase.Id),
            new DownloadTaskMetadata(
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
                downloadBase.ZoneId),
            new DownloadPlan(downloadBase.NeedDownloadContent, [], 0, nfoRequest: null),
            new DownloadOutput(downloadBase.FilePath, downloadBase.FileSize),
            DownloadPhase.Completed,
            DownloadProgress.None,
            DownloadTransferState.Empty,
            null,
            new DownloadCompletion(
                downloaded.FinishedTimestamp,
                downloaded.FinishedTime,
                downloaded.MaxSpeedDisplay),
            0,
            createdAtUtc,
            migratedAtUtc);
    }
}
