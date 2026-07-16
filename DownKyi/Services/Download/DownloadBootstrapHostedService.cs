using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Core.Logging;
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
    private readonly IDownloadRuntimeFactory _downloadRuntimeFactory;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly ILogger<DownloadBootstrapHostedService> _logger;
    private IDownloadRuntime? _downloadRuntime;
    private Task? _historyLoadTask;
    private bool _disposed;

    public DownloadBootstrapHostedService(
        DownloadListState downloadLists,
        DownloadTaskProjectionStore projectionStore,
        IDownloadRuntimeFactory downloadRuntimeFactory,
        IUiDispatcher uiDispatcher,
        ILogger<DownloadBootstrapHostedService> logger)
    {
        _downloadLists = downloadLists ?? throw new ArgumentNullException(nameof(downloadLists));
        _projectionStore = projectionStore
            ?? throw new ArgumentNullException(nameof(projectionStore));
        _downloadRuntimeFactory = downloadRuntimeFactory
            ?? throw new ArgumentNullException(nameof(downloadRuntimeFactory));
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
                _downloadLists.Downloading.AddRange(state.DownloadingItems);
                _downloadLists.Downloaded.AddRange(state.DownloadedItems);
            }).ConfigureAwait(false);

            _historyLoadTask = LoadRemainingHistoryAsync(cancellationToken);
            _downloadRuntime = _downloadRuntimeFactory.Create();
            if (_downloadRuntime != null)
            {
                await _downloadRuntime.StartAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or SqliteException)
        {
            _logger.LogErrorMessage("Download bootstrap failed.", exception);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var stopTasks = new List<Task>(2);
        if (_downloadRuntime != null)
        {
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

    private async Task<DownloadStartupState> LoadStartupStateAsync(CancellationToken cancellationToken)
    {
        var downloadingItemsTask = _projectionStore.GetDownloadingAsync(cancellationToken);
        var downloadedItemsTask = _projectionStore.GetRecentDownloadedAsync(100, cancellationToken);

        await Task.WhenAll(downloadingItemsTask, downloadedItemsTask).ConfigureAwait(false);
        return new DownloadStartupState(
            await downloadingItemsTask.ConfigureAwait(false),
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
                _downloadLists.Downloaded.AddRange(
                    allItems.Where(item => loadedIds.Add(item.DownloadBase.Id)));
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or SqliteException)
        {
            _logger.LogErrorMessage("Remaining download history load failed.", exception);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _downloadRuntime?.Dispose();
        _downloadRuntime = null;
    }

    private sealed record DownloadStartupState(
        IReadOnlyList<DownloadingItem> DownloadingItems,
        IReadOnlyList<DownloadedItem> DownloadedItems);
}
