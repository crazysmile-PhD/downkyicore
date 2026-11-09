namespace DownKyi.Application.Settings;

public interface ISettingsStore : IDisposable, IAsyncDisposable
{
    ApplicationSettings Current { get; }

    ApplicationSettings Update(Func<ApplicationSettings, ApplicationSettings> update);

    Task<SettingsCommitResult> CommitAsync(
        Func<ApplicationSettings, ApplicationSettings> update,
        CancellationToken cancellationToken = default);

    Task FlushAsync(CancellationToken cancellationToken = default);
}
