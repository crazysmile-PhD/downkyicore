namespace DownKyi.Application.Lifetime;

public interface IApplicationLifecycle
{
    Task RequestShutdownAsync(CancellationToken cancellationToken = default);

    Task ExitAsync(CancellationToken cancellationToken = default);

    Task<bool> RestartAsync(CancellationToken cancellationToken = default);
}
