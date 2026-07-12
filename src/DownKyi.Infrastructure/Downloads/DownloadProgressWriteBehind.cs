using System.Threading.Channels;
using DownKyi.Application.Downloads;
using DownKyi.Application.Time;
using DownKyi.Domain.Downloads;

namespace DownKyi.Infrastructure.Downloads;

public sealed class DownloadProgressWriteBehind : IAsyncDisposable
{
    private readonly IDownloadTaskStore _store;
    private readonly IClock _clock;
    private readonly TimeSpan _flushInterval;
    private readonly int _maximumPendingTasks;
    private readonly Lock _pendingLock = new();
    private readonly Dictionary<DownloadTaskId, DownloadProgressWrite> _pending = [];
    private readonly Channel<byte> _signals = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false
    });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private int _disposeState;
    private long _rejectedWriteCount;

    public DownloadProgressWriteBehind(
        IDownloadTaskStore store,
        IClock clock,
        TimeSpan flushInterval,
        int maximumPendingTasks = 256)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        if (flushInterval <= TimeSpan.Zero || flushInterval > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(flushInterval));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPendingTasks, 1);
        _store = store;
        _clock = clock;
        _flushInterval = flushInterval;
        _maximumPendingTasks = maximumPendingTasks;
        _worker = RunAsync();
    }

    public Task Completion => _worker;

    public long RejectedWriteCount => Interlocked.Read(ref _rejectedWriteCount);

    public bool TryQueue(DownloadProgressWrite write)
    {
        ArgumentNullException.ThrowIfNull(write);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        lock (_pendingLock)
        {
            if (_pending.TryGetValue(write.TaskId, out var current))
            {
                _pending[write.TaskId] = current.Merge(write);
            }
            else
            {
                if (_pending.Count >= _maximumPendingTasks)
                {
                    return false;
                }

                _pending.Add(write.TaskId, write);
            }
        }

        _signals.Writer.TryWrite(0);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _signals.Writer.TryComplete();
        await _shutdown.CancelAsync().ConfigureAwait(false);
        try
        {
            await _worker.ConfigureAwait(false);
        }
        finally
        {
            _shutdown.Dispose();
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (await _signals.Reader
                .WaitToReadAsync(_shutdown.Token)
                .ConfigureAwait(false))
            {
                while (_signals.Reader.TryRead(out _))
                {
                }

                await _clock.DelayAsync(_flushInterval, _shutdown.Token).ConfigureAwait(false);
                await FlushPendingAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }

        await FlushPendingAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task FlushPendingAsync(CancellationToken cancellationToken)
    {
        DownloadProgressWrite[] writes;
        lock (_pendingLock)
        {
            writes = [.. _pending.Values];
            _pending.Clear();
        }

        for (var index = 0; index < writes.Length; index++)
        {
            try
            {
                var result = await _store
                    .UpdateProgressAsync(writes[index], cancellationToken)
                    .ConfigureAwait(false);
                if (!result.IsSuccess)
                {
                    Interlocked.Increment(ref _rejectedWriteCount);
                }
            }
            catch
            {
                RestorePending(writes, index);
                throw;
            }
        }
    }

    private void RestorePending(DownloadProgressWrite[] writes, int startIndex)
    {
        lock (_pendingLock)
        {
            for (var index = startIndex; index < writes.Length; index++)
            {
                var write = writes[index];
                _pending[write.TaskId] = _pending.TryGetValue(write.TaskId, out var newer)
                    ? write.Merge(newer)
                    : write;
            }
        }
    }
}
