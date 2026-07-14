namespace DownKyi.Core.Settings;

public interface ISettingsStore
{
    SettingsManager Settings { get; }

    Task FlushAsync(CancellationToken cancellationToken = default);
}

public sealed class SettingsStore : ISettingsStore
{
    public SettingsStore()
        : this(SettingsManager.Instance)
    {
    }

    public SettingsStore(string settingsPath)
        : this(new SettingsManager(settingsPath))
    {
    }

    internal SettingsStore(SettingsManager settings)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public SettingsManager Settings { get; }

    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(Settings.Flush, cancellationToken);
    }
}
