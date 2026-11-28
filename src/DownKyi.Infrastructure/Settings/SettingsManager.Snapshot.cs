using System.Collections.Immutable;
using DownKyi.Application.Diagnostics;
using DownKyi.Application.Settings;

namespace DownKyi.Infrastructure.Settings;

public partial class SettingsManager
{
    internal ApplicationSettings CurrentSnapshot => Volatile.Read(ref _snapshot);

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
            _validateSnapshots = true;
            var validation = ApplicationSettingsValidator.Validate(CreateSnapshotCore());
            snapshot = validation.Settings;
            corrections = validation.Corrections;
            ReplaceSnapshotUnsafe(snapshot, _persistInitialDefaults);
        }

        LogCorrections(corrections);
        return CurrentSnapshot;
    }

    internal ApplicationSettings UpdateSnapshot(Func<ApplicationSettings, ApplicationSettings> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        bool changed;
        ImmutableArray<string> corrections;
        lock (_settingsLock)
        {
            var current = CreateSnapshotCore();
            var candidate = update(current)
                ?? throw new InvalidOperationException("A settings update cannot return null.");
            if (SnapshotValuesEqual(current, candidate))
            {
                return current;
            }
            var validation = ApplicationSettingsValidator.Validate(candidate);
            candidate = validation.Settings;
            corrections = validation.Corrections;
            changed = ReplaceSnapshotUnsafe(candidate);
        }

        LogCorrections(corrections);
        if (changed) Changed?.Invoke();
        return CurrentSnapshot;
    }

    private ApplicationSettings CreateSnapshotCore()
    {
        return _snapshot;
    }

    private bool ReplaceSnapshotUnsafe(ApplicationSettings snapshot, bool markDirty = true)
    {
        if (SnapshotValuesEqual(_snapshot, snapshot))
        {
            return false;
        }

        _appSettings = SettingsSnapshotModelMapper.ToModel(snapshot);
        Volatile.Write(ref _snapshot, snapshot);
        if (markDirty) MarkDirtyUnsafe();
        return true;
    }

    private void RefreshLegacySnapshotUnsafe()
    {
        var snapshot = SettingsSnapshotModelMapper.ToSnapshot(_appSettings, _saveVideoRootPath);
        if (_validateSnapshots)
        {
            var validation = ApplicationSettingsValidator.Validate(snapshot);
            snapshot = validation.Settings;
            _appSettings = SettingsSnapshotModelMapper.ToModel(snapshot);
            LogCorrections(validation.Corrections);
        }
        Volatile.Write(ref _snapshot, snapshot);
    }

    private static bool SnapshotValuesEqual(ApplicationSettings left, ApplicationSettings right)
    {
        if (left == right) return true;
        if (!ContentsEqual(left.Video.HistoryVideoRootPaths, right.Video.HistoryVideoRootPaths) ||
            !ContentsEqual(left.Video.FileNameParts, right.Video.FileNameParts) ||
            !ContentsEqual(left.Danmaku.BlockedKeywords, right.Danmaku.BlockedKeywords) ||
            !ContentsEqual(left.Danmaku.BlockedSenderUids, right.Danmaku.BlockedSenderUids))
        {
            return false;
        }

        // Records compare ImmutableArray members by storage identity. Once their
        // contents match, reuse the right-hand arrays for the remaining value comparison.
        return left with
        {
            Video = left.Video with
            {
                HistoryVideoRootPaths = right.Video.HistoryVideoRootPaths,
                FileNameParts = right.Video.FileNameParts
            },
            Danmaku = left.Danmaku with
            {
                BlockedKeywords = right.Danmaku.BlockedKeywords,
                BlockedSenderUids = right.Danmaku.BlockedSenderUids
            }
        } == right;
    }

    private static bool ContentsEqual<T>(ImmutableArray<T> left, ImmutableArray<T> right)
    {
        return left.IsDefault || right.IsDefault
            ? left.IsDefault && right.IsDefault
            : left.SequenceEqual(right);
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
