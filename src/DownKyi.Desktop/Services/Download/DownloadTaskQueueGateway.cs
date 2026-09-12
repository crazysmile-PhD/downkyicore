using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DownKyi.Domain.Downloads;

namespace DownKyi.Services.Download;

internal sealed class DownloadTaskQueueGateway : IDownloadTaskQueue, IDownloadRuntimeAvailability
{
    private readonly Lock _sync = new();
    private readonly HashSet<DownloadTaskId> _pending = [];
    private readonly TaskCompletionSource<DownloadRuntimeStartupOutcome> _startupOutcome =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IDownloadRuntime? _runtime;
    private DownloadRuntimeState _state = DownloadRuntimeState.Initializing;
    private Exception? _terminalFailure;

    public Task AttachAsync(
        IDownloadRuntime runtime,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_state == DownloadRuntimeState.Faulted)
            {
                throw CreateUnavailableException();
            }

            if (_runtime != null && !ReferenceEquals(_runtime, runtime))
            {
                throw new InvalidOperationException("A download runtime is already attached.");
            }

            _runtime = runtime;
            _state = DownloadRuntimeState.Attaching;
        }

        return Task.CompletedTask;
    }

    public async Task MarkReadyAsync(
        IDownloadRuntime runtime,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        DownloadTaskId[] pending;
        lock (_sync)
        {
            if (!ReferenceEquals(_runtime, runtime)
                || _state != DownloadRuntimeState.Attaching)
            {
                throw new InvalidOperationException("The download runtime is not attached for startup.");
            }

            pending = [.. _pending];
            _pending.Clear();
        }

        try
        {
            while (true)
            {
                foreach (var taskId in pending)
                {
                    await runtime.EnqueueAsync(taskId, cancellationToken).ConfigureAwait(false);
                }

                lock (_sync)
                {
                    if (_pending.Count == 0)
                    {
                        _state = DownloadRuntimeState.Ready;
                        _startupOutcome.TrySetResult(DownloadRuntimeStartupOutcome.Ready());
                        return;
                    }

                    pending = [.. _pending];
                    _pending.Clear();
                }
            }
        }
        catch
        {
            lock (_sync)
            {
                if (ReferenceEquals(_runtime, runtime))
                {
                    _state = DownloadRuntimeState.Attaching;
                }

                foreach (var taskId in pending)
                {
                    _pending.Add(taskId);
                }
            }

            throw;
        }
    }

    public void Detach(IDownloadRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        lock (_sync)
        {
            if (ReferenceEquals(_runtime, runtime))
            {
                _runtime = null;
                if (_state != DownloadRuntimeState.Faulted)
                {
                    _state = DownloadRuntimeState.Initializing;
                }
            }
        }
    }

    public IReadOnlyList<DownloadTaskId> MarkFaulted(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        lock (_sync)
        {
            _runtime = null;
            _state = DownloadRuntimeState.Faulted;
            _terminalFailure ??= exception;
            _startupOutcome.TrySetResult(
                DownloadRuntimeStartupOutcome.Faulted(_terminalFailure));
            var pending = _pending.ToArray();
            _pending.Clear();
            return pending;
        }
    }

    public void EnsureAcceptingTasks()
    {
        lock (_sync)
        {
            if (_state == DownloadRuntimeState.Faulted)
            {
                throw CreateUnavailableException();
            }
        }
    }

    public Task<DownloadRuntimeStartupOutcome> WaitForStartupOutcomeAsync(
        CancellationToken cancellationToken = default)
    {
        return _startupOutcome.Task.WaitAsync(cancellationToken);
    }

    public async Task EnqueueAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        cancellationToken.ThrowIfCancellationRequested();
        IDownloadRuntime? runtime;
        lock (_sync)
        {
            if (_state == DownloadRuntimeState.Faulted)
            {
                throw CreateUnavailableException();
            }

            runtime = _runtime;
            if (_state != DownloadRuntimeState.Ready || runtime == null)
            {
                _pending.Add(taskId);
                return;
            }
        }

        try
        {
            await runtime.EnqueueAsync(taskId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ChannelClosedException or ObjectDisposedException)
        {
            lock (_sync)
            {
                if (ReferenceEquals(_runtime, runtime))
                {
                    _runtime = null;
                    _state = DownloadRuntimeState.Faulted;
                    _terminalFailure ??= exception;
                    _startupOutcome.TrySetResult(
                        DownloadRuntimeStartupOutcome.Faulted(_terminalFailure));
                }
            }

            throw CreateUnavailableException();
        }
    }

    public Task<bool> CancelAsync(DownloadTaskId taskId)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        IDownloadRuntime? runtime;
        lock (_sync)
        {
            runtime = _runtime;
            _pending.Remove(taskId);
        }

        return runtime == null
            ? Task.FromResult(false)
            : runtime.CancelAsync(taskId);
    }

    private DownloadRuntimeUnavailableException CreateUnavailableException()
    {
        var message = _state == DownloadRuntimeState.Faulted
            ? "The download runtime failed to start and is unavailable."
            : "The download runtime is still initializing and is unavailable.";
        return new DownloadRuntimeUnavailableException(message, _terminalFailure);
    }

    private enum DownloadRuntimeState
    {
        Initializing,
        Attaching,
        Ready,
        Faulted
    }

}
