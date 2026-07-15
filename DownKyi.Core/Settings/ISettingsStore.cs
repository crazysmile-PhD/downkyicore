namespace DownKyi.Core.Settings;

public interface ISettingsStore : IDisposable, IAsyncDisposable
{
    ApplicationSettings Current { get; }

    SettingsManager Settings { get; }

    ApplicationSettings Update(Func<ApplicationSettings, ApplicationSettings> update);

    Task FlushAsync(CancellationToken cancellationToken = default);
}

public sealed class SettingsStore : ISettingsStore
{
    private readonly object _updateLock = new();
    private ApplicationSettings _current;
    private bool _isNormalizing;
    private int _disposeState;

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
        var validation = ApplicationSettingsValidator.Validate(settings.CreateInitialSnapshot());
        _current = validation.Settings;
        Settings.Changed += RefreshSnapshot;
        if (validation.Corrections.Length > 0)
        {
            LogCorrections(validation.Corrections);
            Settings.ApplySnapshot(validation.Settings);
        }
    }

    public ApplicationSettings Current => Volatile.Read(ref _current);

    public SettingsManager Settings { get; }

    public ApplicationSettings Update(Func<ApplicationSettings, ApplicationSettings> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_updateLock)
        {
            var candidate = update(Current)
                ?? throw new InvalidOperationException("A settings update cannot return null.");
            var validation = ApplicationSettingsValidator.Validate(candidate);
            if (validation.Corrections.Length > 0)
            {
                LogCorrections(validation.Corrections);
            }

            Settings.ApplySnapshot(validation.Settings);
            Volatile.Write(ref _current, validation.Settings);
            return validation.Settings;
        }
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        return Settings.FlushAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        Settings.Changed -= RefreshSnapshot;
        Settings.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        Settings.Changed -= RefreshSnapshot;
        try
        {
            await Settings.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            await Settings.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void RefreshSnapshot()
    {
        lock (_updateLock)
        {
            var validation = ApplicationSettingsValidator.Validate(Settings.CreateSnapshot());
            if (validation.Corrections.Length > 0)
            {
                LogCorrections(validation.Corrections);
                if (!_isNormalizing)
                {
                    _isNormalizing = true;
                    try
                    {
                        Settings.ApplySnapshot(validation.Settings);
                    }
                    finally
                    {
                        _isNormalizing = false;
                    }
                }
            }

            Volatile.Write(ref _current, validation.Settings);
        }
    }

    private static void LogCorrections(IEnumerable<string> corrections)
    {
        DownKyi.Core.Logging.LogManager.Info(
            nameof(SettingsStore),
            $"Invalid settings were restored to safe defaults: {string.Join(", ", corrections)}");
    }
}
