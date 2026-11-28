using DownKyi.Application.Diagnostics;
using DownKyi.Application.Settings;
using DownKyi.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;

namespace DownKyi.Infrastructure.Settings;

public sealed class SettingsStore : ISettingsStore
{
    private readonly ILogger<SettingsStore> _logger;
    private readonly SettingsManager _settings;
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
        _settings.CreateInitialSnapshot();
    }

    public ApplicationSettings Current => _settings.CurrentSnapshot;

    public ApplicationSettings Update(Func<ApplicationSettings, ApplicationSettings> update)
    {
        return _settings.UpdateSnapshot(update);
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

        _settings.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        try
        {
            await _settings.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            await _settings.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static SettingsManager CreateManager(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        return new SettingsManager(
            ApplicationStorage.GetSettings(),
            loggerFactory.CreateLogger<SettingsManager>());
    }
}
