namespace DownKyi.Infrastructure.Logging;

internal sealed class ApplicationLogRetentionWorker : IAsyncDisposable
{
    private readonly ApplicationLogRetentionManager _retention;
    private readonly Func<DateTimeOffset, string> _getActivePath;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SemaphoreSlim _maintenanceGate = new(1, 1);
    private readonly TaskCompletionSource _startup =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _loopTask;

    public ApplicationLogRetentionWorker(
        ApplicationLogRetentionManager retention,
        Func<DateTimeOffset, string> getActivePath,
        TimeProvider timeProvider,
        TimeSpan interval)
    {
        _retention = retention ?? throw new ArgumentNullException(nameof(retention));
        _getActivePath = getActivePath ?? throw new ArgumentNullException(nameof(getActivePath));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _interval = interval;
        _loopTask = RunAsync();
    }

    public Task Startup => _startup.Task;

    public async Task RunMaintenanceAsync(CancellationToken cancellationToken)
    {
        await _maintenanceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _timeProvider.GetUtcNow().ToUniversalTime();
            await Task.Run(
                    () => _retention.Apply(_getActivePath(now), now),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _maintenanceGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            _startup.TrySetCanceled(_cancellation.Token);
        }

        _maintenanceGate.Dispose();
        _cancellation.Dispose();
    }

    private async Task RunAsync()
    {
        await Task.Yield();
        try
        {
            await RunMaintenanceAsync(_cancellation.Token).ConfigureAwait(false);
            _startup.TrySetResult();
            while (true)
            {
                await Task.Delay(_interval, _timeProvider, _cancellation.Token).ConfigureAwait(false);
                await RunMaintenanceAsync(_cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            _startup.TrySetCanceled(_cancellation.Token);
        }
        catch (Exception exception)
        {
            _startup.TrySetException(exception);
            throw;
        }
    }
}
