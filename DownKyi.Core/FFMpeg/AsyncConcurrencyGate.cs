namespace DownKyi.Core.FFMpeg;

internal sealed class AsyncConcurrencyGate
{
    private readonly object _sync = new();
    private readonly Func<int> _limitProvider;
    private readonly Queue<Waiter> _waiters = new();
    private int _running;

    public AsyncConcurrencyGate(Func<int> limitProvider)
    {
        _limitProvider = limitProvider ?? throw new ArgumentNullException(nameof(limitProvider));
    }

    public ValueTask<Lease> EnterAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_running < GetLimit())
            {
                _running++;
                return ValueTask.FromResult(new Lease(this));
            }

            var waiter = new Waiter(cancellationToken);
            _waiters.Enqueue(waiter);
            return new ValueTask<Lease>(waiter.Task);
        }
    }

    private int GetLimit()
    {
        return Math.Max(1, _limitProvider());
    }

    private void Release()
    {
        lock (_sync)
        {
            _running = Math.Max(0, _running - 1);
            while (_waiters.Count > 0 && _running < GetLimit())
            {
                var waiter = _waiters.Dequeue();
                _running++;
                if (waiter.TrySetResult(new Lease(this)))
                {
                    continue;
                }

                _running--;
            }
        }
    }

    internal readonly struct Lease : IDisposable
    {
        private readonly LeaseState _state;

        public Lease(AsyncConcurrencyGate owner)
        {
            _state = new LeaseState(owner);
        }

        public void Dispose()
        {
            _state.Release();
        }
    }

    private sealed class LeaseState
    {
        private AsyncConcurrencyGate? _owner;

        public LeaseState(AsyncConcurrencyGate owner)
        {
            _owner = owner;
        }

        public void Release()
        {
            Interlocked.Exchange(ref _owner, null)?.Release();
        }
    }

    private sealed class Waiter
    {
        private readonly TaskCompletionSource<Lease> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _registration;

        public Waiter(CancellationToken cancellationToken)
        {
            if (cancellationToken.CanBeCanceled)
            {
                _registration = cancellationToken.Register(
                    static state =>
                    {
                        var tuple = (Tuple<TaskCompletionSource<Lease>, CancellationToken>)state!;
                        tuple.Item1.TrySetCanceled(tuple.Item2);
                    },
                    Tuple.Create(_completion, cancellationToken));
            }
        }

        public Task<Lease> Task => _completion.Task;

        public bool TrySetResult(Lease lease)
        {
            _registration.Dispose();
            return _completion.TrySetResult(lease);
        }
    }
}
