using System;
using System.ComponentModel;
using System.Threading;

namespace DownKyi.Commands;

internal sealed class DownKyiAsyncCommandGate : INotifyPropertyChanged
{
    private int _isExecuting;

    public event EventHandler? IsExecutingChanged;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsExecuting => Volatile.Read(ref _isExecuting) != 0;

    internal bool TryEnter()
    {
        if (Interlocked.CompareExchange(ref _isExecuting, 1, 0) != 0)
        {
            return false;
        }

        IsExecutingChanged?.Invoke(this, EventArgs.Empty);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExecuting)));
        return true;
    }

    internal void Exit()
    {
        if (Interlocked.Exchange(ref _isExecuting, 0) == 0)
        {
            return;
        }

        IsExecutingChanged?.Invoke(this, EventArgs.Empty);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExecuting)));
    }
}
