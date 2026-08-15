using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DownKyi.Domain.Downloads;

namespace DownKyi.Services.Download;

internal sealed class DownloadTaskQueueGateway : IDownloadTaskQueue
{
    private readonly Lock _sync = new();
    private readonly HashSet<DownloadTaskId> _pending = [];
    private IDownloadRuntime? _runtime;

    public async Task AttachAsync(
        IDownloadRuntime runtime,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        DownloadTaskId[] pending;
        lock (_sync)
        {
            if (_runtime != null && !ReferenceEquals(_runtime, runtime))
            {
                throw new InvalidOperationException("A download runtime is already attached.");
            }

            _runtime = runtime;
            pending = [.. _pending];
            _pending.Clear();
        }

        try
        {
            foreach (var taskId in pending)
            {
                await runtime.EnqueueAsync(taskId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            lock (_sync)
            {
                if (ReferenceEquals(_runtime, runtime))
                {
                    _runtime = null;
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
            }
        }
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
            runtime = _runtime;
            if (runtime == null)
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
                }

                _pending.Add(taskId);
            }
        }
    }

    public async Task EnqueueManyAsync(
        IReadOnlyList<DownloadTaskId> taskIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(taskIds);
        cancellationToken.ThrowIfCancellationRequested();

        if (taskIds.Count == 0)
        {
            return;
        }

        foreach (var taskId in taskIds)
        {
            ArgumentNullException.ThrowIfNull(taskId);
        }

        IDownloadRuntime? runtime;

        lock (_sync)
        {
            runtime = _runtime;

            if (runtime == null)
            {
                foreach (var taskId in taskIds)
                {
                    _pending.Add(taskId);
                }

                return;
            }
        }

        var index = 0;

        try
        {
            for (; index < taskIds.Count; index++)
            {
                await runtime
                    .EnqueueAsync(
                        taskIds[index],
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            lock (_sync)
            {
                // Conservative fail-safe:
                //
                // Once a batch enqueue becomes uncertain, stop trusting
                // the currently attached runtime. Persist the uncertain
                // suffix of the batch in the gateway's pending set.
                //
                // DownloadOrchestrator deduplicates task IDs, so replaying
                // the current task is safe even when the runtime accepted
                // it immediately before throwing.
                if (ReferenceEquals(
                        _runtime,
                        runtime))
                {
                    _runtime = null;
                }

                for (var pendingIndex = index;
                     pendingIndex < taskIds.Count;
                     pendingIndex++)
                {
                    _pending.Add(
                        taskIds[pendingIndex]);
                }
            }

            throw;
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

}
