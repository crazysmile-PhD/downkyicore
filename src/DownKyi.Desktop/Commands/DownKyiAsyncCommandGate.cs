using System;
using System.Threading;

namespace DownKyi.Commands;

internal sealed class DownKyiAsyncCommandGate
{
    private int _isExecuting;

    public event EventHandler? IsExecutingChanged;

    public bool IsExecuting => Volatile.Read(ref _isExecuting) != 0;

    internal bool TryEnter()
    {
        if (Interlocked.CompareExchange(ref _isExecuting, 1, 0) != 0)
        {
            return false;
        }

        IsExecutingChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    internal void Exit()
    {
        if (Interlocked.Exchange(ref _isExecuting, 0) != 0)
        {
            IsExecutingChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
