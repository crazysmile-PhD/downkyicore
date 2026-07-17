using System;
using System.Threading.Tasks;
using Avalonia.Input.Platform;
using Avalonia.Threading;

namespace DownKyi.Platform;

internal sealed class AvaloniaClipboardMonitor : IClipboardMonitor
{
    private readonly object _subscriptionLock = new();
    private readonly AvaloniaDesktopContext _desktopContext;
    private int _subscriptionCount;
    private bool _disposed;
    private bool _isTicking;
    private DispatcherTimer? _timer;
    private string? _lastClipboardContent;

    public AvaloniaClipboardMonitor(AvaloniaDesktopContext desktopContext)
    {
        _desktopContext = desktopContext ?? throw new ArgumentNullException(nameof(desktopContext));
    }

    private event EventHandler<ClipboardTextChangedEventArgs>? ChangedImpl;

    public event EventHandler<ClipboardTextChangedEventArgs>? Changed
    {
        add
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_subscriptionLock)
            {
                if (_subscriptionCount == 0)
                {
                    StartTimer();
                }

                _subscriptionCount++;
                ChangedImpl += value;
            }
        }
        remove
        {
            lock (_subscriptionLock)
            {
                ChangedImpl -= value;
                _subscriptionCount = Math.Max(0, _subscriptionCount - 1);
                if (_subscriptionCount == 0)
                {
                    StopTimer();
                }
            }
        }
    }

    private void StartTimer()
    {
        _timer = new DispatcherTimer(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.Background,
            (_, _) => _ = ReadClipboardAsync());
        _timer.Start();
    }

    private void StopTimer()
    {
        _timer?.Stop();
        _timer = null;
    }

    private async Task ReadClipboardAsync()
    {
        if (_disposed || _isTicking)
        {
            return;
        }

        var clipboard = _desktopContext.MainWindow.Clipboard;
        if (clipboard == null)
        {
            return;
        }

        try
        {
            _isTicking = true;
            var currentContent = await clipboard.TryGetTextAsync().ConfigureAwait(true);
            if (string.Equals(currentContent, _lastClipboardContent, StringComparison.Ordinal))
            {
                return;
            }

            if (_lastClipboardContent != null && !string.IsNullOrEmpty(currentContent))
            {
                ChangedImpl?.Invoke(this, new ClipboardTextChangedEventArgs(currentContent));
            }

            _lastClipboardContent = currentContent;
        }
        finally
        {
            _isTicking = false;
        }
    }

    public void Dispose()
    {
        lock (_subscriptionLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopTimer();
            ChangedImpl = null;
            _subscriptionCount = 0;
        }

        GC.SuppressFinalize(this);
    }
}
