using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DownKyi.Core.Logging;
using DownKyi.Core.Settings;
using DownKyi.Models;
using DownKyi.ViewModels.DownloadManager;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

internal sealed class DownloadOrchestrator : IDownloadRuntime
{
    private readonly DownloadPipeline _pipeline;
    private readonly DownloadListState _downloadLists;
    private readonly ISettingsStore _settingsStore;
    private readonly ILogger<DownloadOrchestrator> _logger;
    private readonly Lock _queueLock = new();
    private readonly HashSet<DownloadingItem> _queuedDownloads = [];
    private Channel<DownloadingItem>? _downloadQueue;
    private Task[] _downloadWorkers = [];
    private Task? _dispatchTask;
    private CancellationTokenSource? _tokenSource;
    private bool _disposed;

    public DownloadOrchestrator(
        DownloadPipeline pipeline,
        DownloadListState downloadLists,
        ISettingsStore settingsStore,
        ILogger<DownloadOrchestrator> logger)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _downloadLists = downloadLists ?? throw new ArgumentNullException(nameof(downloadLists));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_tokenSource != null)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await _pipeline.StartAsync(cancellationToken).ConfigureAwait(false);

        _tokenSource = new CancellationTokenSource();
        var workerCount = Math.Max(1, _settingsStore.Current.Network.MaxCurrentDownloads);
        _downloadQueue = Channel.CreateBounded<DownloadingItem>(new BoundedChannelOptions(
            Math.Max(32, workerCount * 8))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = workerCount == 1,
            SingleWriter = true
        });
        _downloadWorkers = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Run(() => DownloadWorkerAsync(
                _downloadQueue.Reader,
                _tokenSource.Token)))
            .ToArray();
        _dispatchTask = Task.Run(DispatchAsync, CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_tokenSource == null)
        {
            return;
        }

        await DownloadShutdownCoordinator.StopAsync(
            _tokenSource,
            _dispatchTask,
            _downloadWorkers,
            TimeSpan.FromSeconds(30),
            exception => _logger.LogErrorMessage(
                "Download workers failed during shutdown.",
                exception),
            _pipeline.PersistShutdownStateAsync).ConfigureAwait(false);

        await _pipeline.StopAsync(cancellationToken).ConfigureAwait(false);
        _tokenSource.Dispose();
        _tokenSource = null;
        _dispatchTask = null;
        _downloadQueue = null;
        _downloadWorkers = [];
        lock (_queueLock)
        {
            _queuedDownloads.Clear();
        }
    }

    private async Task DispatchAsync()
    {
        var queue = _downloadQueue ?? throw new InvalidOperationException("Download queue is not initialized.");
        var token = _tokenSource?.Token ?? CancellationToken.None;
        try
        {
            while (!token.IsCancellationRequested)
            {
                foreach (var downloading in _downloadLists.Downloading)
                {
                    if (downloading.Downloading.DownloadStatus is not (
                            DownloadStatus.NotStarted or DownloadStatus.WaitForDownload) ||
                        !TryMarkQueued(downloading))
                    {
                        continue;
                    }

                    try
                    {
                        await queue.Writer.WriteAsync(downloading, token).ConfigureAwait(false);
                    }
                    catch
                    {
                        UnmarkQueued(downloading);
                        throw;
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (InvalidOperationException exception)
        {
            _logger.LogErrorMessage("Download work loop failed.", exception);
        }
        finally
        {
            queue.Writer.TryComplete();
        }
    }

    private async Task DownloadWorkerAsync(
        ChannelReader<DownloadingItem> reader,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var downloading in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    if (!_downloadLists.Downloading.Contains(downloading) ||
                        downloading.Downloading.DownloadStatus is not (
                            DownloadStatus.NotStarted or DownloadStatus.WaitForDownload))
                    {
                        continue;
                    }

                    downloading.Downloading.DownloadStatus = DownloadStatus.Downloading;
                    await _pipeline.PersistDownloadingStateAsync(downloading).ConfigureAwait(false);
                    await _pipeline.ExecuteAsync(downloading, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                    or InvalidOperationException or HttpRequestException or Newtonsoft.Json.JsonException)
                {
                    _logger.LogErrorMessage("Download worker failed.", exception);
                    await _pipeline.MarkFailedAsync(downloading).ConfigureAwait(false);
                }
                finally
                {
                    UnmarkQueued(downloading);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private bool TryMarkQueued(DownloadingItem downloading)
    {
        lock (_queueLock)
        {
            return _queuedDownloads.Add(downloading);
        }
    }

    private void UnmarkQueued(DownloadingItem downloading)
    {
        lock (_queueLock)
        {
            _queuedDownloads.Remove(downloading);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tokenSource?.Cancel();
        _tokenSource?.Dispose();
        _tokenSource = null;
        _pipeline.Dispose();
    }
}
