using System;
using System.Threading.Tasks;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using DownKyi.Application.Settings;

namespace DownKyi.Platform;

internal sealed class AvaloniaClipboardMonitor : IClipboardMonitor
{
    private readonly object _subscriptionLock = new();
    private readonly Func<Task<string?>> _readTextAsync;
    private int _subscriptionCount;
    private bool _disposed;
    private bool _isTicking;
    private bool _enabled;
    private int _generation;
    private DispatcherTimer? _timer;
    private string? _lastClipboardContent;

    public AvaloniaClipboardMonitor(AvaloniaDesktopContext desktopContext, ISettingsStore settingsStore)
        : this(settingsStore, () => ReadTextAsync(desktopContext))
    {
        ArgumentNullException.ThrowIfNull(desktopContext);
    }

    internal AvaloniaClipboardMonitor(ISettingsStore settingsStore, Func<Task<string?>> readTextAsync)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        _readTextAsync = readTextAsync ?? throw new ArgumentNullException(nameof(readTextAsync));
        _enabled = settingsStore.Current.Basic.IsListenClipboard == AllowStatus.Yes;
    }

    public void SetEnabled(bool enabled)
    {
        lock (_subscriptionLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_enabled == enabled) return;
            _enabled = enabled;
            _generation++;
            _lastClipboardContent = null;
            if (_enabled && _subscriptionCount > 0) StartTimer();
            else StopTimer();
        }
    }

    private event EventHandler<ClipboardTextChangedEventArgs>? ChangedImpl;

    public event EventHandler<ClipboardTextChangedEventArgs>? Changed
    {
        add
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_subscriptionLock)
            {
                if (_subscriptionCount == 0 && _enabled)
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
            async (_, _) => await ReadClipboardAsync().ConfigureAwait(true));
        _timer.Start();
    }

    private void StopTimer()
    {
        _generation++;
        _timer?.Stop();
        _timer = null;
        _lastClipboardContent = null;
    }

    private async Task ReadClipboardAsync()
    {
        if (_disposed || !_enabled || _isTicking || _subscriptionCount == 0)
        {
            return;
        }

        try
        {
            _isTicking = true;
            var generation = _generation;
            var currentContent = await _readTextAsync().ConfigureAwait(true);
            if (_disposed || !_enabled || generation != _generation) return;
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

    private static async Task<string?> ReadTextAsync(AvaloniaDesktopContext desktopContext)
    {
        var clipboard = desktopContext.MainWindow.Clipboard;
        return clipboard == null ? null : await clipboard.TryGetTextAsync().ConfigureAwait(true);
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
