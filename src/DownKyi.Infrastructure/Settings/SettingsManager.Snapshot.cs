using System.Collections.Immutable;
using DownKyi.Application.Diagnostics;
using DownKyi.Application.Settings;
using Newtonsoft.Json;

namespace DownKyi.Infrastructure.Settings;

public partial class SettingsManager
{
    internal ApplicationSettings CreateSnapshot()
    {
        lock (_settingsLock)
        {
            return CreateSnapshotCore();
        }
    }

    internal ApplicationSettings CreateInitialSnapshot()
    {
        ApplicationSettings snapshot;
        ImmutableArray<string> corrections;
        lock (_settingsLock)
        {
            var validation = ApplicationSettingsValidator.Validate(CreateSnapshotCore());
            snapshot = validation.Settings;
            corrections = validation.Corrections;
            ReplaceSnapshotUnsafe(snapshot, _persistInitialDefaults);
        }

        LogCorrections(corrections);
        return snapshot;
    }

    internal void UpdateSnapshot(Func<ApplicationSettings, ApplicationSettings> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        ApplicationSettings candidate;
        ImmutableArray<string> corrections;
        lock (_settingsLock)
        {
            candidate = update(CreateSnapshotCore())
                ?? throw new InvalidOperationException("A settings update cannot return null.");
            var validation = ApplicationSettingsValidator.Validate(candidate);
            candidate = validation.Settings;
            corrections = validation.Corrections;
            ReplaceSnapshotUnsafe(candidate);
        }

        LogCorrections(corrections);
        Changed?.Invoke();
    }

    private ApplicationSettings CreateSnapshotCore()
    {
        return SettingsSnapshotModelMapper.ToSnapshot(_appSettings, _saveVideoRootPath);
    }

    internal void ApplySnapshot(ApplicationSettings snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var validated = ApplicationSettingsValidator.Validate(snapshot).Settings;
        lock (_settingsLock)
        {
            ReplaceSnapshotUnsafe(validated);
        }

        Changed?.Invoke();
    }

    private void ReplaceSnapshotUnsafe(ApplicationSettings snapshot, bool markDirty = true)
    {
        var replacement = SettingsSnapshotModelMapper.ToModel(snapshot);
        var replacementJson = JsonConvert.SerializeObject(replacement);
        var currentJson = JsonConvert.SerializeObject(_appSettings);
        if (!string.Equals(currentJson, replacementJson, StringComparison.Ordinal))
        {
            _appSettings = replacement;
            if (markDirty)
            {
                MarkDirtyUnsafe();
            }
        }
    }

    private void LogCorrections(ImmutableArray<string> corrections)
    {
        if (corrections.Length > 0)
        {
            _logger.LogInformationMessage(
                $"Invalid settings were restored to safe defaults: {string.Join(", ", corrections)}");
        }
    }
}
