using System.Security.Cryptography;
using System.Text;
using DownKyi.Core.Logging;
using DownKyi.Core.Settings.Models;
using DownKyi.Core.Utils.Encryptor;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;

namespace DownKyi.Core.Settings;

public sealed partial class SettingsManager : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan FlushDelay = TimeSpan.FromMilliseconds(750);
    private readonly object _settingsLock = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly string _settingsName;
    private readonly bool _persistInitialDefaults;
    private readonly ILogger<SettingsManager> _logger;
    private readonly string password = "YO1J$4#p";
    private AppSettings _appSettings;
    private Timer? _flushTimer;
    private bool _dirty;
    private bool _persistenceDisabled;
    private long _changeVersion;
    private int _suppressChangeNotifications;
    private int _suppressPersistence;
    private int _disposeState;

    internal event Action? Changed;

    internal SettingsManager(string settingsName)
        : this(settingsName, NullLogger<SettingsManager>.Instance)
    {
    }

    internal SettingsManager(string settingsName, ILogger<SettingsManager> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsName);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _settingsName = settingsName;
        var loadResult = LoadFromFile();
        _appSettings = loadResult.Settings;
        _persistInitialDefaults = loadResult.PersistInitialDefaults;
        _persistenceDisabled = loadResult.DisablePersistence;
        EnsureSections(_appSettings);
        if (loadResult.PreserveInvalidFile && !TryPreserveInvalidSettingsFile())
        {
            _persistenceDisabled = true;
        }

        var migration = SettingsSchemaMigrator.Migrate(_appSettings);
        if (migration.Migrated && loadResult.PersistInitialDefaults)
        {
            MarkDirty();
        }

        if (migration.IsFutureSchema)
        {
            _persistenceDisabled = true;
            _logger.LogErrorMessage(
                $"Settings schema {_appSettings.SchemaVersion} is newer than supported schema {ApplicationSettingsValidator.CurrentSchemaVersion}; persistence is disabled to preserve the file.");
        }
    }

    private bool SetProperty<T>(T currentValue, T newValue, Action<T> setter)
    {
        ArgumentNullException.ThrowIfNull(setter);
        var changed = false;
        lock (_settingsLock)
        {
            if (!EqualityComparer<T>.Default.Equals(currentValue, newValue))
            {
                setter(newValue);
                if (_suppressPersistence == 0)
                {
                    MarkDirtyUnsafe();
                }
                changed = true;
            }
        }

        if (changed && _suppressChangeNotifications == 0)
        {
            Changed?.Invoke();
        }

        return true;
    }

    private SettingsLoadResult LoadFromFile()
    {
        if (!File.Exists(_settingsName))
        {
            return SettingsLoadResult.NewFile();
        }

        string payload;
        try
        {
            payload = File.ReadAllText(_settingsName, Encoding.UTF8);
        }
        catch (IOException e)
        {
            LogLoadFailure("Settings file could not be read", e);
            return SettingsLoadResult.Unreadable();
        }
        catch (UnauthorizedAccessException e)
        {
            LogLoadFailure("Settings file access was denied", e);
            return SettingsLoadResult.Unreadable();
        }

        try
        {
            var settings = JsonConvert.DeserializeObject<AppSettings>(payload);
            return settings == null
                ? SettingsLoadResult.Invalid()
                : SettingsLoadResult.Loaded(settings);
        }
        catch (JsonException)
        {
            return LoadLegacySettings(payload);
        }
    }

    private SettingsLoadResult LoadLegacySettings(string payload)
    {
        try
        {
            var decrypted = LegacySettingsDecryptor.Decrypt(payload, password);
            var settings = JsonConvert.DeserializeObject<AppSettings>(decrypted);
            return settings == null
                ? SettingsLoadResult.Invalid()
                : SettingsLoadResult.Loaded(settings);
        }
        catch (CryptographicException e)
        {
            LogLoadFailure("Legacy settings decryption failed", e);
        }
        catch (FormatException e)
        {
            LogLoadFailure("Legacy settings format is invalid", e);
        }
        catch (ArgumentException e)
        {
            LogLoadFailure("Legacy settings arguments are invalid", e);
        }
        catch (JsonException e)
        {
            LogLoadFailure("Settings JSON is invalid", e);
        }

        return SettingsLoadResult.Invalid();
    }

    private bool TryPreserveInvalidSettingsFile()
    {
        try
        {
            var backupPath = $"{_settingsName}.invalid-{Guid.NewGuid():N}";
            File.Move(_settingsName, backupPath);
            _logger.LogInformationMessage(
                "Invalid settings were preserved before safe defaults were initialized.");
            return true;
        }
        catch (IOException e)
        {
            LogLoadFailure("Invalid settings could not be preserved; persistence is disabled", e);
            return false;
        }
        catch (UnauthorizedAccessException e)
        {
            LogLoadFailure("Invalid settings could not be preserved; persistence is disabled", e);
            return false;
        }
    }

    private static void EnsureSections(AppSettings settings)
    {
        settings.Basic ??= new BasicSettings();
        settings.Network ??= new NetworkSettings();
        settings.Video ??= new VideoSettings();
        settings.Danmaku ??= new DanmakuSettings();
        settings.About ??= new AboutSettings();
        settings.UserInfo ??= new UserInfoSettings();
        settings.WindowSettings ??= new WindowSettings();
    }

    private void LogLoadFailure(string message, Exception exception)
    {
        _logger.LogErrorMessage(message, exception);
    }

    private sealed record SettingsLoadResult(
        AppSettings Settings,
        bool PreserveInvalidFile,
        bool DisablePersistence,
        bool PersistInitialDefaults)
    {
        public static SettingsLoadResult NewFile()
        {
            return new SettingsLoadResult(
                new AppSettings(),
                PreserveInvalidFile: false,
                DisablePersistence: false,
                PersistInitialDefaults: false);
        }

        public static SettingsLoadResult Loaded(AppSettings settings)
        {
            return new SettingsLoadResult(
                settings,
                PreserveInvalidFile: false,
                DisablePersistence: false,
                PersistInitialDefaults: true);
        }

        public static SettingsLoadResult Invalid()
        {
            return new SettingsLoadResult(
                new AppSettings(),
                PreserveInvalidFile: true,
                DisablePersistence: false,
                PersistInitialDefaults: true);
        }

        public static SettingsLoadResult Unreadable()
        {
            return new SettingsLoadResult(
                new AppSettings(),
                PreserveInvalidFile: false,
                DisablePersistence: true,
                PersistInitialDefaults: false);
        }
    }

    private void MarkDirty()
    {
        lock (_settingsLock)
        {
            MarkDirtyUnsafe();
        }
    }

    private void MarkDirtyUnsafe()
    {
        if (_persistenceDisabled || Volatile.Read(ref _disposeState) != 0)
        {
            return;
        }

        _dirty = true;
        _changeVersion = checked(_changeVersion + 1);
        _flushTimer ??= new Timer(
            static state => _ = ((SettingsManager)state!).FlushDebouncedAsync(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        _flushTimer.Change(FlushDelay, Timeout.InfiniteTimeSpan);
    }

    private async Task FlushDebouncedAsync()
    {
        try
        {
            await FlushAsync().ConfigureAwait(false);
        }
        catch (IOException e)
        {
            _logger.LogErrorMessage("Debounced settings persistence failed.", e);
        }
        catch (UnauthorizedAccessException e)
        {
            _logger.LogErrorMessage("Debounced settings persistence was denied.", e);
        }
        catch (JsonException e)
        {
            _logger.LogErrorMessage("Debounced settings serialization failed.", e);
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_settingsLock)
        {
            _flushTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                string json;
                long version;
                lock (_settingsLock)
                {
                    if (!_dirty || _persistenceDisabled)
                    {
                        return;
                    }

                    json = JsonConvert.SerializeObject(_appSettings);
                    version = _changeVersion;
                }

                await WriteSettingsFileAsync(json, cancellationToken).ConfigureAwait(false);
                lock (_settingsLock)
                {
                    if (_changeVersion == version)
                    {
                        _dirty = false;
                        return;
                    }
                }
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task WriteSettingsFileAsync(string json, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_settingsName);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempFile = $"{_settingsName}.{Guid.NewGuid():N}.tmp";
        try
        {
            var stream = new FileStream(
                tempFile,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (stream.ConfigureAwait(false))
            {
                var writer = new StreamWriter(
                    stream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    bufferSize: 1024,
                    leaveOpen: true);
                await using (writer.ConfigureAwait(false))
                {
                    await writer.WriteAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(_settingsName))
            {
                File.Replace(tempFile, _settingsName, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempFile, _settingsName);
            }
        }
        finally
        {
            try
            {
                File.Delete(tempFile);
            }
            catch (IOException e)
            {
                _logger.LogErrorMessage("Temporary settings file cleanup failed.", e);
            }
            catch (UnauthorizedAccessException e)
            {
                _logger.LogErrorMessage("Temporary settings file cleanup was denied.", e);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        Timer? timer;
        lock (_settingsLock)
        {
            timer = _flushTimer;
            _flushTimer = null;
        }

        timer?.Dispose();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        Timer? timer;
        lock (_settingsLock)
        {
            timer = _flushTimer;
            _flushTimer = null;
        }

        if (timer != null)
        {
            await timer.DisposeAsync().ConfigureAwait(false);
        }

        await _writeGate.WaitAsync().ConfigureAwait(false);
        _writeGate.Release();
        _writeGate.Dispose();
        GC.SuppressFinalize(this);
    }
}
