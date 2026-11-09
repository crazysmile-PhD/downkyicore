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
    private readonly SemaphoreSlim _commitGate = new(1, 1);
    private readonly ILogger<SettingsStore> _logger;
    private readonly SettingsManager _settings;
    private ApplicationSettings _current;
    private bool _isNormalizing;
    private long _updateRevision;
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

            _settings.ApplySnapshot(validation.Settings);
            Volatile.Write(ref _current, validation.Settings);
            return validation.Settings;
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
        await _commitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ApplicationSettings candidate;
                long persistenceVersion;
                long revision;
                lock (_updateLock)
                {
                    candidate = update(Current)
                        ?? throw new InvalidOperationException("A settings update cannot return null.");
                    var validation = ApplicationSettingsValidator.Validate(candidate);
                    if (validation.Corrections.Length > 0)
                    {
                        LogCorrections(validation.Corrections);
                    }

                    candidate = validation.Settings;
                    persistenceVersion = _settings.CapturePersistenceVersion();
                    revision = _updateRevision;
                }

                SettingsSnapshotPersistenceStatus persistenceStatus;
                try
                {
                    persistenceStatus = await _settings
                        .PersistSnapshotAsync(candidate, persistenceVersion, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
                {
                    _logger.LogErrorMessage("Durable settings commit failed.", e);
                    return Failed(SettingsCommitFailure.WriteFailed);
                }

                if (persistenceStatus == SettingsSnapshotPersistenceStatus.Disabled)
                {
                    _logger.LogErrorMessage("Durable settings commit is unavailable because persistence is disabled.");
                    return Failed(SettingsCommitFailure.PersistenceDisabled);
                }

                if (persistenceStatus == SettingsSnapshotPersistenceStatus.Superseded)
                {
                    continue;
                }

                lock (_updateLock)
                {
                    if (_updateRevision != revision)
                    {
                        continue;
                    }

                    Volatile.Write(ref _current, candidate);
                    _updateRevision = checked(_updateRevision + 1);
                    return new SettingsCommitResult(candidate, SettingsCommitFailure.None);
                }
            }
        }
        finally
        {
            _commitGate.Release();
        }

        SettingsCommitResult Failed(SettingsCommitFailure failure)
        {
            return new SettingsCommitResult(Current, failure);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _settings.Changed -= RefreshSnapshot;
        _settings.Dispose();
        _commitGate.Dispose();
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
            try
            {
                await _settings.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _commitGate.Dispose();
            }
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
            _updateRevision = checked(_updateRevision + 1);
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
