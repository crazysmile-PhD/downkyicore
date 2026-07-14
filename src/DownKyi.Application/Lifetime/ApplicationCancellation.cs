namespace DownKyi.Application.Lifetime;

public sealed class ApplicationCancellation : IDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private bool _disposed;

    public CancellationToken ShutdownToken => _shutdown.Token;

    public CancellationTokenSource CreateOperationScope(CancellationToken operationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, operationToken);
    }

    public Task RequestShutdownAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _shutdown.CancelAsync();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _shutdown.Dispose();
        _disposed = true;
    }
}
