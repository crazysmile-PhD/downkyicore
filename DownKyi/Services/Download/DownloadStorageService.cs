using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Downloads;
using DownKyi.Application.Time;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Domain.Downloads;
using DownKyi.Models;
using DownKyi.ViewModels.DownloadManager;
using DomainDownloadTask = DownKyi.Domain.Downloads.DownloadTask;
using DomainQuality = DownKyi.Domain.Downloads.DownloadQuality;
using LegacyDownloadStatus = DownKyi.Models.DownloadStatus;
using LegacyQuality = DownKyi.Core.BiliApi.BiliUtils.Quality;

namespace DownKyi.Services.Download;

/// <summary>
/// Projects the new download domain into legacy UI models while the ViewModels are migrated.
/// Persistence belongs exclusively to <see cref="IDownloadTaskStore"/>.
/// </summary>
internal sealed class DownloadStorageService : IDisposable
{
    private const int MaximumUpdateAttempts = 2;
    private readonly IDownloadTaskStore _store;
    private readonly IClock _clock;
    private readonly ConcurrentDictionary<string, DomainDownloadTask> _snapshots = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _taskGates = new(StringComparer.Ordinal);
    private bool _disposed;

    public DownloadStorageService(IDownloadTaskStore store, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        _store = store;
        _clock = clock;
    }

    public async Task AddDownloadingAsync(
        DownloadingItem? downloadingItem,
        CancellationToken cancellationToken = default)
    {
        if (downloadingItem?.DownloadBase == null)
        {
            return;
        }

        var task = CreateUnfinishedTask(downloadingItem, _clock.UtcNow);
        var result = await _store.AddAsync(task, cancellationToken).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            _snapshots[task.Id.Value] = task;
            return;
        }

        var existing = await _store.FindAsync(task.Id, cancellationToken).ConfigureAwait(true);
        if (existing != null)
        {
            _snapshots[task.Id.Value] = existing;
            return;
        }

        ThrowStoreFailure(result.Error?.Message);
    }

    public async Task RemoveDownloadingAsync(
        DownloadingItem? downloadingItem,
        bool cascadeRemove = false,
        CancellationToken cancellationToken = default)
    {
        if (downloadingItem?.DownloadBase == null || !cascadeRemove)
        {
            // A non-cascading removal is followed by AddDownloadedAsync. The new store performs
            // that table move atomically when the completed aggregate is persisted.
            return;
        }

        await DeleteAsync(downloadingItem.DownloadBase.Id, cancellationToken).ConfigureAwait(true);
    }

    public async Task<IReadOnlyList<DownloadingItem>> GetDownloadingAsync(
        CancellationToken cancellationToken = default)
    {
        var tasks = await _store.GetUnfinishedAsync(cancellationToken).ConfigureAwait(true);
        foreach (var task in tasks)
        {
            _snapshots[task.Id.Value] = task;
        }

        return tasks.Select(ToDownloadingItem).ToArray();
    }

    public async Task UpdateDownloadingAsync(
        DownloadingItem? downloadingItem,
        CancellationToken cancellationToken = default)
    {
        if (downloadingItem?.DownloadBase == null)
        {
            return;
        }

        var id = downloadingItem.DownloadBase.Id;
        var gate = GetTaskGate(id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            for (var attempt = 0; attempt < MaximumUpdateAttempts; attempt++)
            {
                var current = await GetCurrentTaskAsync(id, cancellationToken).ConfigureAwait(true);
                if (current == null)
                {
                    await AddDownloadingAsync(downloadingItem, cancellationToken).ConfigureAwait(true);
                    return;
                }

                var updated = CreateUnfinishedTask(
                    downloadingItem,
                    current.CreatedAtUtc,
                    checked(current.Version + 1),
                    LaterOf(_clock.UtcNow, current.UpdatedAtUtc),
                    current.Progress);
                var result = await _store
                    .UpdateAsync(updated, current.Version, cancellationToken)
                    .ConfigureAwait(true);
                if (result.IsSuccess)
                {
                    _snapshots[id] = updated;
                    return;
                }

                _snapshots.TryRemove(id, out _);
            }

            ThrowStoreFailure($"Download task '{id}' changed while it was being saved.");
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task AddDownloadedAsync(
        DownloadedItem? downloadedItem,
        CancellationToken cancellationToken = default)
    {
        if (downloadedItem?.DownloadBase == null)
        {
            return;
        }

        var id = downloadedItem.DownloadBase.Id;
        var gate = GetTaskGate(id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            for (var attempt = 0; attempt < MaximumUpdateAttempts; attempt++)
            {
                var current = await GetCurrentTaskAsync(id, cancellationToken).ConfigureAwait(true);
                if (current == null)
                {
                    var completed = CreateCompletedTask(downloadedItem, _clock.UtcNow);
                    var addResult = await _store.AddAsync(completed, cancellationToken).ConfigureAwait(true);
                    if (addResult.IsSuccess)
                    {
                        _snapshots[id] = completed;
                        return;
                    }
                }
                else
                {
                    var completed = CreateCompletedTask(
                        downloadedItem,
                        current.CreatedAtUtc,
                        checked(current.Version + 1),
                        LaterOf(_clock.UtcNow, current.UpdatedAtUtc),
                        current.Progress,
                        current.Transfer,
                        current.Plan);
                    var updateResult = await _store
                        .UpdateAsync(completed, current.Version, cancellationToken)
                        .ConfigureAwait(true);
                    if (updateResult.IsSuccess)
                    {
                        _snapshots[id] = completed;
                        return;
                    }
                }

                _snapshots.TryRemove(id, out _);
            }

            ThrowStoreFailure($"Completed download '{id}' could not be saved.");
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task AddDownloadedBatchAsync(
        IEnumerable<Downloaded> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.DownloadBase == null)
            {
                continue;
            }

            var completed = CreateCompletedTask(item, item.DownloadBase, _clock.UtcNow);
            var result = await _store.AddAsync(completed, cancellationToken).ConfigureAwait(true);
            if (result.IsSuccess)
            {
                _snapshots[completed.Id.Value] = completed;
                continue;
            }

            var existing = await _store.FindAsync(completed.Id, cancellationToken).ConfigureAwait(true);
            if (existing == null)
            {
                ThrowStoreFailure(result.Error?.Message);
            }

            if (existing.Phase == DownloadPhase.Completed)
            {
                _snapshots[existing.Id.Value] = existing;
                continue;
            }

            completed = CreateCompletedTask(
                item,
                item.DownloadBase,
                existing.CreatedAtUtc,
                checked(existing.Version + 1),
                LaterOf(_clock.UtcNow, existing.UpdatedAtUtc),
                existing.Progress,
                existing.Transfer,
                existing.Plan);
            result = await _store
                .UpdateAsync(completed, existing.Version, cancellationToken)
                .ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                ThrowStoreFailure(result.Error?.Message);
            }

            _snapshots[completed.Id.Value] = completed;
        }
    }

    public async Task RemoveDownloadedAsync(
        DownloadedItem? downloadedItem,
        CancellationToken cancellationToken = default)
    {
        if (downloadedItem?.DownloadBase == null)
        {
            return;
        }

        await DeleteAsync(downloadedItem.DownloadBase.Id, cancellationToken).ConfigureAwait(true);
    }

    public async Task<DownloadHistoryPage> GetDownloadedPageAsync(
        DownloadHistoryCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var page = await _store
            .GetHistoryPageAsync(cursor, pageSize, cancellationToken)
            .ConfigureAwait(true);
        foreach (var task in page.Items)
        {
            _snapshots[task.Id.Value] = task;
        }

        return page;
    }

    public async Task<IReadOnlyList<DownloadedItem>> GetDownloadedAsync(
        CancellationToken cancellationToken = default)
    {
        var items = new List<DownloadedItem>();
        DownloadHistoryCursor? cursor = null;
        do
        {
            var page = await GetDownloadedPageAsync(cursor, 500, cancellationToken).ConfigureAwait(true);
            items.AddRange(page.Items.Select(ToDownloadedItem));
            cursor = page.NextCursor;
        }
        while (cursor != null);

        return items;
    }

    public async Task<IReadOnlyList<DownloadedItem>> GetRecentDownloadedAsync(
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var page = await GetDownloadedPageAsync(null, pageSize, cancellationToken).ConfigureAwait(true);
        return page.Items.Select(ToDownloadedItem).ToArray();
    }

    public Task UpdateDownloadedAsync(
        DownloadedItem? downloadedItem,
        CancellationToken cancellationToken = default)
    {
        return AddDownloadedAsync(downloadedItem, cancellationToken);
    }

    public async Task ClearDownloadedAsync(CancellationToken cancellationToken = default)
    {
        var result = await _store.ClearHistoryAsync(cancellationToken).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            ThrowStoreFailure(result.Error?.Message);
        }

        foreach (var snapshot in _snapshots.Where(item => item.Value.Phase == DownloadPhase.Completed).ToArray())
        {
            _snapshots.TryRemove(snapshot.Key, out _);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var gate in _taskGates.Values)
        {
            gate.Dispose();
        }

        _taskGates.Clear();
        _disposed = true;
    }

    internal static DownloadingItem ToDownloadingItem(DomainDownloadTask task)
    {
        var downloadBase = ToDownloadBase(task);
        var downloading = new Downloading
        {
            Id = task.Id.Value,
            Gid = task.Transfer.BackendIdentity,
            DownloadFiles = task.Plan.TransferFiles.ToDictionary(item => item.Key, item => item.Value),
            DownloadedFiles = task.Transfer.CompletedFileKeys.ToList(),
            PlayStreamType = (PlayStreamType)task.Plan.StreamType,
            DownloadStatus = ToLegacyStatus(task.Phase),
            DownloadContent = task.Transfer.ActiveContent,
            DownloadStatusTitle = task.Transfer.StatusText,
            Progress = checked((float)task.Progress.Percentage),
            DownloadingFileSize = task.Progress.DownloadedSizeText,
            MaxSpeed = task.Transfer.MaximumBytesPerSecond,
            SpeedDisplay = task.Progress.SpeedText,
            DownloadBase = downloadBase
        };
        return new DownloadingItem { DownloadBase = downloadBase, Downloading = downloading };
    }

    internal static DownloadedItem ToDownloadedItem(DomainDownloadTask task)
    {
        var completion = task.Completion
            ?? throw new InvalidOperationException("Completed download is missing completion details.");
        var downloadBase = ToDownloadBase(task);
        var downloaded = new Downloaded
        {
            Id = task.Id.Value,
            MaxSpeedDisplay = completion.MaximumSpeedText,
            FinishedTimestamp = completion.FinishedTimestamp,
            FinishedTime = completion.FinishedTimeText,
            DownloadBase = downloadBase
        };
        return new DownloadedItem { DownloadBase = downloadBase, Downloaded = downloaded };
    }

    private async Task DeleteAsync(string id, CancellationToken cancellationToken)
    {
        var taskId = new DownloadTaskId(id);
        var result = await _store.DeleteAsync(taskId, cancellationToken).ConfigureAwait(true);
        if (!result.IsSuccess && result.Error?.Code != "download.store.not_found")
        {
            ThrowStoreFailure(result.Error?.Message);
        }

        _snapshots.TryRemove(id, out _);
    }

    private async Task<DomainDownloadTask?> GetCurrentTaskAsync(
        string id,
        CancellationToken cancellationToken)
    {
        if (_snapshots.TryGetValue(id, out var snapshot))
        {
            return snapshot;
        }

        var loaded = await _store.FindAsync(new DownloadTaskId(id), cancellationToken).ConfigureAwait(true);
        if (loaded != null)
        {
            _snapshots[id] = loaded;
        }

        return loaded;
    }

    private SemaphoreSlim GetTaskGate(string id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _taskGates.GetOrAdd(id, static _ => new SemaphoreSlim(1, 1));
    }

    private static DomainDownloadTask CreateUnfinishedTask(
        DownloadingItem item,
        DateTimeOffset createdAtUtc,
        long version = 0,
        DateTimeOffset? updatedAtUtc = null,
        DownloadProgress? existingProgress = null)
    {
        var downloading = item.Downloading;
        var phase = ToDomainPhase(downloading.DownloadStatus);
        var failure = phase == DownloadPhase.Failed
            ? new DownloadFailure(
                "download.legacy.failed",
                downloading.DownloadStatusTitle ?? "Download failed.",
                true)
            : null;
        return DomainDownloadTask.Restore(
            new DownloadTaskId(item.DownloadBase.Id),
            ToMetadata(item.DownloadBase),
            new DownloadPlan(
                item.DownloadBase.NeedDownloadContent,
                downloading.DownloadFiles,
                (int)downloading.PlayStreamType),
            new DownloadOutput(item.DownloadBase.FilePath, item.DownloadBase.FileSize),
            phase,
            new DownloadProgress(
                downloading.Progress,
                existingProgress?.DownloadedBytes,
                existingProgress?.TotalBytes,
                existingProgress?.BytesPerSecond ?? 0,
                downloadedSizeText: downloading.DownloadingFileSize,
                speedText: downloading.SpeedDisplay),
            new DownloadTransferState(
                downloading.Gid,
                downloading.DownloadedFiles,
                downloading.DownloadContent,
                downloading.DownloadStatusTitle,
                downloading.MaxSpeed),
            failure,
            null,
            version,
            createdAtUtc,
            updatedAtUtc ?? createdAtUtc);
    }

    private static DomainDownloadTask CreateCompletedTask(
        DownloadedItem item,
        DateTimeOffset createdAtUtc,
        long version = 0,
        DateTimeOffset? updatedAtUtc = null,
        DownloadProgress? progress = null,
        DownloadTransferState? transfer = null,
        DownloadPlan? plan = null)
    {
        return CreateCompletedTask(
            item.Downloaded,
            item.DownloadBase,
            createdAtUtc,
            version,
            updatedAtUtc,
            progress,
            transfer,
            plan);
    }

    private static DomainDownloadTask CreateCompletedTask(
        Downloaded downloaded,
        DownloadBase downloadBase,
        DateTimeOffset createdAtUtc,
        long version = 0,
        DateTimeOffset? updatedAtUtc = null,
        DownloadProgress? progress = null,
        DownloadTransferState? transfer = null,
        DownloadPlan? plan = null)
    {
        return DomainDownloadTask.Restore(
            new DownloadTaskId(downloadBase.Id),
            ToMetadata(downloadBase),
            plan ?? new DownloadPlan(downloadBase.NeedDownloadContent, [], 0),
            new DownloadOutput(downloadBase.FilePath, downloadBase.FileSize),
            DownloadPhase.Completed,
            progress ?? DownloadProgress.None,
            transfer ?? DownloadTransferState.Empty,
            null,
            new DownloadCompletion(
                downloaded.FinishedTimestamp,
                downloaded.FinishedTime,
                downloaded.MaxSpeedDisplay),
            version,
            createdAtUtc,
            updatedAtUtc ?? createdAtUtc);
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
            ToDomainQuality(downloadBase.Resolution),
            ToDomainQuality(downloadBase.AudioCodec),
            downloadBase.CoverUrl,
            downloadBase.PageCoverUrl,
            downloadBase.ZoneId);
    }

    private static DownloadBase ToDownloadBase(DomainDownloadTask task)
    {
        return new DownloadBase
        {
            Id = task.Id.Value,
            NeedDownloadContent = task.Plan.RequestedAssets.ToDictionary(item => item.Key, item => item.Value),
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
            Resolution = ToLegacyQuality(task.Metadata.Resolution),
            AudioCodec = ToLegacyQuality(task.Metadata.AudioCodec),
            FilePath = task.Output.BasePath,
            FileSize = task.Output.FileSizeText,
            Page = task.Metadata.Media.Page
        };
    }

    private static DomainQuality ToDomainQuality(LegacyQuality quality)
    {
        return new DomainQuality(quality.Id, quality.Name);
    }

    private static LegacyQuality ToLegacyQuality(DomainQuality quality)
    {
        return new LegacyQuality { Id = quality.Id, Name = quality.Name };
    }

    private static DownloadPhase ToDomainPhase(LegacyDownloadStatus status)
    {
        return status switch
        {
            LegacyDownloadStatus.PauseStarted => DownloadPhase.Pausing,
            LegacyDownloadStatus.Pause => DownloadPhase.Paused,
            LegacyDownloadStatus.Downloading => DownloadPhase.Downloading,
            LegacyDownloadStatus.DownloadFailed => DownloadPhase.Failed,
            _ => DownloadPhase.Queued
        };
    }

    private static LegacyDownloadStatus ToLegacyStatus(DownloadPhase phase)
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

    private static DateTimeOffset LaterOf(DateTimeOffset first, DateTimeOffset second)
    {
        return first >= second ? first : second;
    }

    [DoesNotReturn]
    private static void ThrowStoreFailure(string? message)
    {
        throw new InvalidOperationException(message ?? "Download storage operation failed.");
    }
}
