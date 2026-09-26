using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DownKyi.Application.Lifetime;
using Microsoft.Extensions.Hosting;

namespace DownKyi.Services.Media;

internal sealed class ContentDownloadBatchOwner : IContentDownloadCoordinator, IHostedService, IDisposable
{
    private readonly IContentDownloadCoordinator _coordinator;
    private readonly ApplicationCancellation _applicationCancellation;
    private readonly Channel<BatchRequest> _requests = Channel.CreateUnbounded<BatchRequest>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    private CancellationTokenRegistration _shutdownRegistration;
    private Task? _workerTask;
    private int _started;
    private bool _disposed;

    public ContentDownloadBatchOwner(
        ContentDownloadCoordinator coordinator,
        ApplicationCancellation applicationCancellation)
        : this(
            (IContentDownloadCoordinator)(coordinator
                ?? throw new ArgumentNullException(nameof(coordinator))),
            applicationCancellation)
    {
    }

    internal ContentDownloadBatchOwner(
        IContentDownloadCoordinator coordinator,
        ApplicationCancellation applicationCancellation)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _applicationCancellation = applicationCancellation
            ?? throw new ArgumentNullException(nameof(applicationCancellation));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            return Task.CompletedTask;
        }

        _shutdownRegistration = _applicationCancellation.ShutdownToken.Register(
            static state => ((ChannelWriter<BatchRequest>)state!).TryComplete(),
            _requests.Writer);
        _workerTask = ProcessQueueAsync();
        return Task.CompletedTask;
    }

    public async Task<int?> AddAsync(
        IReadOnlyList<ContentDownloadItem> items,
        bool onlySelected,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(items);
        cancellationToken.ThrowIfCancellationRequested();
        _applicationCancellation.ShutdownToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _started) == 0)
        {
            throw new InvalidOperationException("Content download batch owner has not started.");
        }

        var operationScope = _applicationCancellation.CreateOperationScope(cancellationToken);
        var request = new BatchRequest(items.ToArray(), onlySelected, operationScope);
        if (!_requests.Writer.TryWrite(request))
        {
            request.ReleaseOperationScope();
            _applicationCancellation.ShutdownToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Content download batch owner is no longer accepting work.");
        }

        return await request.Completion.ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _applicationCancellation.RequestShutdownAsync().ConfigureAwait(false);
        _requests.Writer.TryComplete();
        var workerTask = Volatile.Read(ref _workerTask);
        if (workerTask != null)
        {
            await workerTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _requests.Writer.TryComplete();
        _shutdownRegistration.Dispose();
    }

    private async Task ProcessQueueAsync()
    {
        await foreach (var request in _requests.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                if (!request.TryStart())
                {
                    continue;
                }

                var result = await _coordinator.AddAsync(
                    request.Items,
                    request.OnlySelected,
                    request.CancellationToken).ConfigureAwait(false);
                request.TrySetResult(result);
            }
            catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested)
            {
                request.TrySetCanceled();
            }
            catch (Exception exception) when (IsRecoverableBatchFailure(exception))
            {
                request.TrySetException(exception);
            }
            finally
            {
                request.ReleaseOperationScope();
            }
        }
    }

    private static bool IsRecoverableBatchFailure(Exception exception) =>
        exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException;

    private sealed class BatchRequest
    {
        private const int Queued = 0;
        private const int Running = 1;
        private const int Completed = 2;
        private readonly CancellationTokenSource _operationScope;
        private readonly CancellationTokenRegistration _cancellationRegistration;
        private readonly TaskCompletionSource<int?> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _state = Queued;

        public BatchRequest(
            IReadOnlyList<ContentDownloadItem> items,
            bool onlySelected,
            CancellationTokenSource operationScope)
        {
            Items = items;
            OnlySelected = onlySelected;
            _operationScope = operationScope;
            _cancellationRegistration = CancellationToken.Register(
                static state => ((BatchRequest)state!).CancelIfQueued(),
                this);
        }

        public IReadOnlyList<ContentDownloadItem> Items { get; }

        public bool OnlySelected { get; }

        public CancellationToken CancellationToken => _operationScope.Token;

        public Task<int?> Completion => _completion.Task;

        public bool TryStart()
        {
            return Interlocked.CompareExchange(ref _state, Running, Queued) == Queued;
        }

        public void TrySetResult(int? result)
        {
            if (Interlocked.CompareExchange(ref _state, Completed, Running) == Running)
            {
                _completion.TrySetResult(result);
            }
        }

        public void TrySetCanceled()
        {
            if (Interlocked.CompareExchange(ref _state, Completed, Running) == Running)
            {
                _completion.TrySetCanceled(CancellationToken);
            }
        }

        public void TrySetException(Exception exception)
        {
            if (Interlocked.CompareExchange(ref _state, Completed, Running) == Running)
            {
                _completion.TrySetException(exception);
            }
        }

        public void ReleaseOperationScope()
        {
            _cancellationRegistration.Dispose();
            _operationScope.Dispose();
        }

        private void CancelIfQueued()
        {
            if (Interlocked.CompareExchange(ref _state, Completed, Queued) == Queued)
            {
                _completion.TrySetCanceled(CancellationToken);
            }
        }
    }
}
