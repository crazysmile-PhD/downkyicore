using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Diagnostics;
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
        ILogger<DownloadBootstrapHostedService> logger)
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
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var state = await LoadStartupStateAsync(cancellationToken).ConfigureAwait(false);
            await _uiDispatcher.InvokeAsync(() =>
            {
                _downloadLists.AddDownloadingRange(state.DownloadingItems);
                _downloadLists.AddDownloadedRange(state.DownloadedItems);
            }).ConfigureAwait(false);

            _historyLoadTask = LoadRemainingHistoryAsync(cancellationToken);
            _downloadRuntime = _downloadRuntimeFactory.Create();
            if (_downloadRuntime != null)
            {
                await _downloadRuntime.StartAsync(cancellationToken).ConfigureAwait(false);
                await _queueGateway
                    .AttachAsync(_downloadRuntime, cancellationToken)
                    .ConfigureAwait(false);
                await QueueStartupTasksAsync(
                    state.UnfinishedTasks,
                    _downloadRuntime,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CleanupFailedRuntimeAsync().ConfigureAwait(false);
            return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or SqliteException)
        {
            await CleanupFailedRuntimeAsync().ConfigureAwait(false);
            _logger.LogErrorMessage("Download bootstrap failed.", exception);
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or SqliteException)
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
                    .RecoverInterruptedAsync(taskId, cancellationToken)
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
