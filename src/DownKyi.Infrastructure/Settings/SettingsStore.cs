using DownKyi.Application.Diagnostics;
using DownKyi.Application.Settings;
using DownKyi.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;

namespace DownKyi.Infrastructure.Settings;

public sealed class SettingsStore : ISettingsStore
{
    private readonly object _updateLock = new();
    private readonly ILogger<SettingsStore> _logger;
    private readonly SettingsManager _settings;
    private ApplicationSettings _current;
    private bool _isNormalizing;
    private int _disposeState;

    public SettingsStore(ILoggerFactory loggerFactory)
        : this(
            CreateManager(loggerFactory),
            loggerFactory.CreateLogger<SettingsStore>())
    {
    }

    public SettingsStore(string settingsPath)
        : this(
            new SettingsManager(settingsPath, NullLogger<SettingsManager>.Instance),
            NullLogger<SettingsStore>.Instance)
    {
    }

    internal SettingsStore(SettingsManager settings)
        : this(settings, NullLogger<SettingsStore>.Instance)
    {
    }

    internal SettingsStore(SettingsManager settings, ILogger<SettingsStore> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var validation = ApplicationSettingsValidator.Validate(settings.CreateInitialSnapshot());
        _current = validation.Settings;
        _settings.Changed += RefreshSnapshot;
        if (validation.Corrections.Length > 0)
        {
            LogCorrections(validation.Corrections);
            _settings.ApplySnapshot(validation.Settings);
        }
    }

    public ApplicationSettings Current => Volatile.Read(ref _current);

    public ApplicationSettings Update(Func<ApplicationSettings, ApplicationSettings> update)
    {
        lock (_updateLock)
        {
            _settings.UpdateSnapshot(update);
            return Current;
        }
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        return _settings.FlushAsync(cancellationToken);
    }

    public async Task<SettingsCommitResult> CommitAsync(
        Func<ApplicationSettings, ApplicationSettings> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        SettingsCommitFailure failure;
        try
        {
            failure = await _settings.CommitAsync(update, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogErrorMessage("Durable settings commit failed.", e);
            return new SettingsCommitResult(Current, SettingsCommitFailure.WriteFailed);
        }

        if (failure == SettingsCommitFailure.PersistenceDisabled)
        {
            _logger.LogErrorMessage("Durable settings commit is unavailable because persistence is disabled.");
        }

        return new SettingsCommitResult(Current, failure);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _settings.Changed -= RefreshSnapshot;
        _settings.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _settings.Changed -= RefreshSnapshot;
        try
        {
            await _settings.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            await _settings.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void RefreshSnapshot()
    {
        lock (_updateLock)
        {
            var validation = ApplicationSettingsValidator.Validate(_settings.CreateSnapshot());
            if (validation.Corrections.Length > 0)
            {
                LogCorrections(validation.Corrections);
                if (!_isNormalizing)
                {
                    _isNormalizing = true;
                    try
                    {
                        _settings.ApplySnapshot(validation.Settings);
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

    private void LogCorrections(IEnumerable<string> corrections)
    {
        _logger.LogInformationMessage(
            $"Invalid settings were restored to safe defaults: {string.Join(", ", corrections)}");
    }

    private static SettingsManager CreateManager(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        return new SettingsManager(
            ApplicationStorage.GetSettings(),
            loggerFactory.CreateLogger<SettingsManager>());
    }
}
