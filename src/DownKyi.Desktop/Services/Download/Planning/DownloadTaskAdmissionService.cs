using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;
using DownKyi.ViewModels.DownloadManager;

namespace DownKyi.Services.Download;

internal sealed record DownloadDuplicateReview(
    IReadOnlySet<string> ActiveDuplicateIds,
    IReadOnlySet<string> CompletedDuplicateIds);

internal sealed class DownloadTaskAdmissionService : IDisposable
{
    private readonly DownloadListState _downloadLists;
    private readonly IDownloadOutputReservationService _outputReservations;
    private readonly IDownloadTaskLookup _tasks;
    private readonly IDownloadHistoryService _history;
    private readonly DownloadTaskProjectionStore _projections;
    private readonly DownloadCommittedTaskDispatcher _committedTaskDispatcher;
    private readonly IDownloadRuntimeAvailability _runtimeAvailability;
    private readonly IPhysicalOutputPathResolver _physicalOutputPathResolver;
    private readonly SemaphoreSlim _admissionGate = new(1, 1);
    private bool _disposed;

    public DownloadTaskAdmissionService(
        DownloadListState downloadLists,
        IDownloadOutputReservationService outputReservations,
        IDownloadTaskLookup tasks,
        IDownloadHistoryService history,
        DownloadTaskProjectionStore projections,
        DownloadCommittedTaskDispatcher committedTaskDispatcher,
        IDownloadRuntimeAvailability runtimeAvailability,
        IPhysicalOutputPathResolver physicalOutputPathResolver)
    {
        _downloadLists = downloadLists ?? throw new ArgumentNullException(nameof(downloadLists));
        _outputReservations = outputReservations
            ?? throw new ArgumentNullException(nameof(outputReservations));
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _projections = projections ?? throw new ArgumentNullException(nameof(projections));
        _committedTaskDispatcher = committedTaskDispatcher
            ?? throw new ArgumentNullException(nameof(committedTaskDispatcher));
        _runtimeAvailability = runtimeAvailability
            ?? throw new ArgumentNullException(nameof(runtimeAvailability));
        _physicalOutputPathResolver = physicalOutputPathResolver
            ?? throw new ArgumentNullException(nameof(physicalOutputPathResolver));
    }

    public async Task<bool> AdmitAsync(
        DownloadingItem item,
        bool autoAddNumberSuffix,
        CancellationToken cancellationToken,
        bool redownloadCompleted = false)
    {
        ArgumentNullException.ThrowIfNull(item);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _admissionGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            var cid = item.DownloadBase.Cid;
            var duplicateKey = DuplicateKey.From(cid, item.DownloadBase.NeedDownloadContent);
            var active = await _tasks
                .GetUnfinishedByCidAsync(cid, cancellationToken)
                .ConfigureAwait(true);
            if (active.Any(existing => DuplicateKey.From(
                    existing.Metadata.Media.Cid, existing.Plan.RequestedContent) == duplicateKey))
            {
                return false;
            }

            if (!redownloadCompleted)
            {
                var completed = await _history
                    .GetByCidAsync(cid, cancellationToken)
                    .ConfigureAwait(true);
                if (completed.Any(existing => DuplicateKey.From(
                        existing.Cid, existing.EffectiveRequestedContent) == duplicateKey))
                {
                    return false;
                }
            }

            _runtimeAvailability.EnsureAcceptingTasks();

            var physicalBasePath = _physicalOutputPathResolver.ResolvePhysicalBasePath(
                item.DownloadBase.FilePath);
            var requestedClaims = DownloadActionClaims.From(
                item.DownloadBase.NeedDownloadContent,
                includesNfo: item.Metadata != null);
            var admittedBasePath = await DownloadOutputPathResolver.ResolveAdmissionCollisionAsync(
                physicalBasePath,
                autoAddNumberSuffix,
                (path, token) => _outputReservations.HasOutputClaimConflictAsync(
                    path,
                    item.DownloadBase.Cid,
                    requestedClaims,
                    DownloadOutputPathKey.UsesCaseInsensitiveComparison,
                    token),
                token => _outputReservations.GetActiveOutputReservationKeysAsync(
                    DownloadOutputPathKey.UsesCaseInsensitiveComparison,
                    token),
                cancellationToken,
                path => DownloadOutputPathResolver.HasExistingOutputClaimConflict(
                    path,
                    requestedClaims)).ConfigureAwait(true);
            item.DownloadBase.FilePath = admittedBasePath;

            await _projections.AddDownloadingAsync(item, cancellationToken).ConfigureAwait(true);

            // Once persisted, admission must finish even if the originating UI operation is canceled.
            _downloadLists.AddDownloading(item);
            var taskId = new DownloadTaskId(item.DownloadBase.Id);
            await _committedTaskDispatcher.EnqueueCommittedAsync(taskId).ConfigureAwait(true);
            return true;
        }
        finally
        {
            _admissionGate.Release();
        }
    }

    public async Task<DownloadDuplicateReview> ReviewDuplicatesAsync(
        IReadOnlyList<DownloadingItem> items,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (items.Count == 0)
        {
            return new DownloadDuplicateReview(new HashSet<string>(), new HashSet<string>());
        }

        var cids = items.Select(item => item.DownloadBase.Cid).Distinct().ToArray();
        var active = await _tasks
            .GetUnfinishedByCidsAsync(cids, cancellationToken)
            .ConfigureAwait(true);
        var completed = await _history
            .GetByCidsAsync(cids, cancellationToken)
            .ConfigureAwait(true);
        var activeKeys = active.Select(task => DuplicateKey.From(
            task.Metadata.Media.Cid, task.Plan.RequestedContent)).ToHashSet();
        var completedKeys = completed.Select(history => DuplicateKey.From(
            history.Cid, history.EffectiveRequestedContent)).ToHashSet();
        var incomingKeys = new HashSet<DuplicateKey>();
        var activeDuplicateIds = new HashSet<string>(StringComparer.Ordinal);
        var completedDuplicateIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var key = DuplicateKey.From(item.DownloadBase.Cid, item.DownloadBase.NeedDownloadContent);
            if (activeKeys.Contains(key) || !incomingKeys.Add(key))
            {
                activeDuplicateIds.Add(item.DownloadBase.Id);
            }
            else if (completedKeys.Contains(key))
            {
                completedDuplicateIds.Add(item.DownloadBase.Id);
            }
        }

        return new DownloadDuplicateReview(activeDuplicateIds, completedDuplicateIds);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _admissionGate.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private readonly record struct DuplicateKey(
        long Cid,
        bool Audio,
        bool Video,
        bool Danmaku,
        bool Subtitle,
        bool Cover)
    {
        public static DuplicateKey From(long cid, DownloadContentSelection content)
        {
            return new DuplicateKey(
                cid,
                content.Audio,
                content.Video,
                content.Danmaku,
                content.Subtitle,
                content.Cover);
        }
    }
}
