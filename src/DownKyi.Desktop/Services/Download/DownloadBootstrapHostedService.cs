using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Diagnostics;
using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;
using DownKyi.Platform;
using DownKyi.ViewModels.DownloadManager;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

internal sealed class DownloadBootstrapHostedService : IHostedService, IDisposable
{
    private readonly DownloadListState _downloadLists;
    private readonly DownloadTaskProjectionStore _projectionStore;
    private readonly DownloadTaskStateWriter _stateWriter;
    private readonly IDownloadRuntimeFactory _downloadRuntimeFactory;
    private readonly DownloadTaskQueueGateway _queueGateway;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly ILogger<DownloadBootstrapHostedService> _logger;
    private readonly DownloadTaskStaging? _staging;
    private readonly DownloadTaskFileService? _fileService;
    private IDownloadRuntime? _downloadRuntime;
    private Task? _historyLoadTask;
    private bool _disposed;

    public DownloadBootstrapHostedService(
        DownloadListState downloadLists,
        DownloadTaskProjectionStore projectionStore,
        DownloadTaskStateWriter stateWriter,
        IDownloadRuntimeFactory downloadRuntimeFactory,
        DownloadTaskQueueGateway queueGateway,
        IUiDispatcher uiDispatcher,
        ILogger<DownloadBootstrapHostedService> logger,
        DownloadTaskStaging? staging = null,
        DownloadTaskFileService? fileService = null)
    {
        _downloadLists = downloadLists ?? throw new ArgumentNullException(nameof(downloadLists));
        _projectionStore = projectionStore
            ?? throw new ArgumentNullException(nameof(projectionStore));
        _stateWriter = stateWriter ?? throw new ArgumentNullException(nameof(stateWriter));
        _downloadRuntimeFactory = downloadRuntimeFactory
            ?? throw new ArgumentNullException(nameof(downloadRuntimeFactory));
        _queueGateway = queueGateway ?? throw new ArgumentNullException(nameof(queueGateway));
        _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _staging = staging;
        _fileService = fileService;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<DownloadTask> startupTasks = [];
        try
        {
            var state = await LoadStartupStateAsync(cancellationToken).ConfigureAwait(false);
            var blocked = new HashSet<DownloadTaskId>();
            if (_fileService != null)
            {
                foreach (var task in state.UnfinishedTasks.Where(task =>
                             task.Output.PublishingArtifact != null))
                {
                    try
                    {
                        var (_, canRun) = await _fileService
                            .ReconcilePublishingAsync(task, cancellationToken)
                            .ConfigureAwait(false);
                        if (!canRun)
                        {
                            blocked.Add(task.Id);
                            if (task.Phase is DownloadPhase.Queued or DownloadPhase.Downloading
                                or DownloadPhase.Pausing)
                            {
                                await _stateWriter.FailAsync(task.Id, new DownloadFailure(
                                        "download.publish.reconcile",
                                        "Pending publication could not be safely reconciled.", true),
                                    cancellationToken).ConfigureAwait(false);
                            }
                        }
                    }
                    catch (Exception exception) when (IsRecoverableBoundaryFailure(exception))
                    {
                        blocked.Add(task.Id);
                        _logger.LogErrorMessage("Pending publication recovery failed.", exception);
                    }
                }

                state = await LoadStartupStateAsync(cancellationToken).ConfigureAwait(false);
            }

            startupTasks = state.UnfinishedTasks.Where(task => !blocked.Contains(task.Id)).ToArray();
            if (_staging != null)
            {
                var knownTasks = new List<DownloadTask>(state.UnfinishedTasks);
                DownloadHistoryCursor? cursor = null;
                do
                {
                    var page = await _projectionStore.GetDownloadedPageAsync(
                        cursor, 500, cancellationToken).ConfigureAwait(false);
                    knownTasks.AddRange(page.Items);
                    cursor = page.NextCursor;
                }
                while (cursor != null);

                _staging.CleanupStale(knownTasks);
            }
            await _uiDispatcher.InvokeAsync(() =>
            {
                _downloadLists.AddDownloadingRange(state.DownloadingItems);
                _downloadLists.AddDownloadedRange(state.DownloadedItems);
            }).ConfigureAwait(false);

            _historyLoadTask = LoadRemainingHistoryAsync(cancellationToken);
            _downloadRuntime = _downloadRuntimeFactory.Create()
                ?? throw new InvalidOperationException("The download runtime factory returned no runtime.");
            await _downloadRuntime.StartAsync(cancellationToken).ConfigureAwait(false);
            await _queueGateway
                .AttachAsync(_downloadRuntime, cancellationToken)
                .ConfigureAwait(false);
            await QueueStartupTasksAsync(
                startupTasks,
                _downloadRuntime,
                cancellationToken).ConfigureAwait(false);
            await _queueGateway
                .MarkReadyAsync(_downloadRuntime, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CleanupFailedRuntimeAsync().ConfigureAwait(false);
            return;
        }
        catch (Exception exception) when (IsRecoverableBoundaryFailure(exception))
        {
            _logger.LogErrorMessage("Download bootstrap failed.", exception);
            var pendingTasks = _queueGateway.MarkFaulted(exception);
            await CleanupFailedRuntimeAsync().ConfigureAwait(false);
            var ownedTaskIds = startupTasks
                .Select(task => task.Id)
                .Concat(pendingTasks)
                .Distinct()
                .ToArray();
            await MarkOwnedTasksFailedAsync(ownedTaskIds).ConfigureAwait(false);
        }
    }

    private async Task MarkOwnedTasksFailedAsync(IReadOnlyList<DownloadTaskId> taskIds)
    {
        foreach (var taskId in taskIds)
        {
            try
            {
                await _stateWriter.FailRuntimeUnavailableIfDispatchableAsync(
                    taskId,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsRecoverableBoundaryFailure(exception))
            {
                _logger.LogErrorMessage(
                    "Owned download task could not be failed after bootstrap failure.",
                    exception);
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var stopTasks = new List<Task>(2);
        if (_downloadRuntime != null)
        {
            _queueGateway.Detach(_downloadRuntime);
            stopTasks.Add(_downloadRuntime.StopAsync(cancellationToken));
        }

        if (_historyLoadTask != null)
        {
            stopTasks.Add(_historyLoadTask);
        }

        if (stopTasks.Count > 0)
        {
            await Task.WhenAll(stopTasks).WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        _staging?.CleanupCurrentSession();
    }

    private async Task CleanupFailedRuntimeAsync()
    {
        var runtime = _downloadRuntime;
        if (runtime == null)
        {
            return;
        }

        _queueGateway.Detach(runtime);
        try
        {
            await runtime.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRecoverableBoundaryFailure(exception))
        {
            _logger.LogErrorMessage("Failed download runtime cleanup also failed.", exception);
        }
        finally
        {
            runtime.Dispose();
            _downloadRuntime = null;
        }
    }

    private async Task<DownloadStartupState> LoadStartupStateAsync(CancellationToken cancellationToken)
    {
        var downloadingStateTask = _projectionStore.GetDownloadingStateAsync(cancellationToken);
        var downloadedItemsTask = _projectionStore.GetRecentDownloadedAsync(100, cancellationToken);

        await Task.WhenAll(downloadingStateTask, downloadedItemsTask).ConfigureAwait(false);
        var downloadingState = await downloadingStateTask.ConfigureAwait(false);
        return new DownloadStartupState(
            downloadingState.Tasks,
            downloadingState.Projections,
            await downloadedItemsTask.ConfigureAwait(false));
    }

    private static bool IsRecoverableBoundaryFailure(Exception exception) =>
        exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException;

    private async Task LoadRemainingHistoryAsync(CancellationToken cancellationToken)
    {
        try
        {
            var allItems = await _projectionStore
                .GetDownloadedAsync(cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await _uiDispatcher.InvokeAsync(() =>
            {
                var loadedIds = _downloadLists.Downloaded
                    .Select(item => item.DownloadBase.Id)
                    .ToHashSet(StringComparer.Ordinal);
                _downloadLists.AddDownloadedRange(
                    allItems.Where(item => loadedIds.Add(item.DownloadBase.Id)));
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or SqliteException)
        {
            _logger.LogErrorMessage("Remaining download history load failed.", exception);
        }
    }

    private async Task QueueStartupTasksAsync(
        IReadOnlyList<DownloadTask> tasks,
        IDownloadRuntime runtime,
        CancellationToken cancellationToken)
    {
        foreach (var restoredTask in tasks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var taskId = restoredTask.Id;
            var task = restoredTask;
            if (task.Phase is DownloadPhase.Downloading or DownloadPhase.Pausing)
            {
                task = await _stateWriter
                    .ReconcileInterruptedAsync(
                        taskId,
                        restoredTask.Version,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (task.Phase == DownloadPhase.Queued)
            {
                await runtime.EnqueueAsync(taskId, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_downloadRuntime != null)
        {
            _queueGateway.Detach(_downloadRuntime);
        }

        _downloadRuntime?.Dispose();
        _downloadRuntime = null;
    }

    private sealed record DownloadStartupState(
        IReadOnlyList<DownloadTask> UnfinishedTasks,
        IReadOnlyList<DownloadingItem> DownloadingItems,
        IReadOnlyList<DownloadedItem> DownloadedItems);
}
