using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using MicrosoftLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace DownKyi.Core.Logging;

public sealed class ApplicationLogProvider : ILoggerProvider, ISupportExternalScope, IApplicationLogService, IAsyncDisposable
{
    private const string DiagnosticDirectoryName = "Diagnostics";
    private const string EventFilePattern = "events-*.jsonl";
    private readonly ApplicationLogOptions _options;
    private readonly ISensitiveDataRedactor _redactor;
    private readonly TimeProvider _timeProvider;
    private readonly Channel<QueueItem> _queue;
    private readonly Queue<ApplicationLogRecord> _recentEvents;
    private readonly object _recentLock = new();
    private readonly object _diagnosticLock = new();
    private readonly HashSet<string> _activeDiagnosticDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Task _writerTask;
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();
    private Exception? _writerFailure;
    private int _disposeState;
    private long _droppedEntryCount;
    private long _bytesWritten;
    private long _eventsWritten;
    private long _ageDeletionCount;
    private long _capacityDeletionCount;
    private long _maintenanceFailureCount;
    private long _retainedBytes;
    private long _lastMaintenanceUtcTicks;

    public ApplicationLogProvider(ApplicationLogOptions options)
        : this(options, new SensitiveDataRedactor(), TimeProvider.System)
    {
    }

    internal ApplicationLogProvider(
        ApplicationLogOptions options,
        ISensitiveDataRedactor redactor,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        if (string.IsNullOrWhiteSpace(options.LogDirectory))
        {
            throw new ArgumentException("A log directory is required.", nameof(options));
        }

        if (options.QueueCapacity <= 0
            || options.RecentEventCapacity <= 0
            || options.MaxFileBytes <= 0
            || options.MaxTotalBytes <= 0
            || options.MaxRetainedAge <= TimeSpan.Zero
            || options.MaintenanceInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Logging limits must be positive.");
        }

        _options = options with { LogDirectory = Path.GetFullPath(options.LogDirectory) };
        _recentEvents = new Queue<ApplicationLogRecord>(options.RecentEventCapacity);
        _queue = Channel.CreateBounded<QueueItem>(new BoundedChannelOptions(options.QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _writerTask = ProcessQueueAsync();
    }

    public string LogDirectory => _options.LogDirectory;

    internal IExternalScopeProvider ScopeProvider => Volatile.Read(ref _scopeProvider);

    public ILogger CreateLogger(string categoryName)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        return new ApplicationLogger(this, categoryName);
    }

    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        ArgumentNullException.ThrowIfNull(scopeProvider);
        Volatile.Write(ref _scopeProvider, scopeProvider);
    }

    public IReadOnlyList<ApplicationLogRecord> GetRecentEvents()
    {
        lock (_recentLock)
        {
            return _recentEvents.ToArray();
        }
    }

    public ApplicationLogMetrics GetMetrics()
    {
        var retainedBytes = Interlocked.Read(ref _retainedBytes);
        var lastMaintenanceTicks = Interlocked.Read(ref _lastMaintenanceUtcTicks);
        return new ApplicationLogMetrics(
            Interlocked.Read(ref _bytesWritten),
            Interlocked.Read(ref _eventsWritten),
            Interlocked.Read(ref _droppedEntryCount),
            Interlocked.Read(ref _ageDeletionCount),
            Interlocked.Read(ref _capacityDeletionCount),
            Interlocked.Read(ref _maintenanceFailureCount),
            retainedBytes,
            (double)retainedBytes / _options.MaxTotalBytes,
            lastMaintenanceTicks == 0
                ? null
                : new DateTimeOffset(lastMaintenanceTicks, TimeSpan.Zero));
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposeState) != 0)
        {
            await _writerTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await _queue.Writer.WriteAsync(new FlushItem(completion), cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException exception) when (exception.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
        }

        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ExportDiagnosticLogAsync(CancellationToken cancellationToken = default)
    {
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        await RequestMaintenanceAsync(cancellationToken).ConfigureAwait(false);

        var timestamp = _timeProvider.GetUtcNow().ToUniversalTime();
        var diagnosticDirectory = ReserveDiagnosticDirectory(timestamp);
        var exportCompleted = false;
        try
        {
            var recent = GetRecentEvents();
            var eventsPath = Path.Combine(diagnosticDirectory, "events.jsonl");
            var stream = new FileStream(
                eventsPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (stream.ConfigureAwait(false))
            {
                var writer = new StreamWriter(stream, new UTF8Encoding(false));
                await using (writer.ConfigureAwait(false))
                {
                    foreach (var entry in recent)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await writer.WriteLineAsync(SerializeRecord(entry).AsMemory(), cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            var manifest = new ApplicationDiagnosticManifest(
                SchemaVersion: 1,
                GeneratedAtUtc: timestamp,
                ApplicationVersion: typeof(ApplicationLogProvider).Assembly.GetName().Version?.ToString() ?? "unknown",
                Runtime: Environment.Version.ToString(),
                OperatingSystem: _redactor.Redact(RuntimeInformation.OSDescription),
                Architecture: RuntimeInformation.ProcessArchitecture.ToString(),
                EventCount: recent.Count,
                Files: ["events.jsonl"],
                Redaction:
                [
                    "cookies and request secrets",
                    "email addresses and user identifiers",
                    "personal directory prefixes"
                ],
                Storage: GetMetrics());
            var manifestPath = Path.Combine(diagnosticDirectory, "manifest.json");
            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(manifest, ApplicationLogJsonContext.Default.ApplicationDiagnosticManifest),
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);
            Interlocked.Add(
                ref _retainedBytes,
                new FileInfo(eventsPath).Length + new FileInfo(manifestPath).Length);
            exportCompleted = true;
            return manifestPath;
        }
        finally
        {
            lock (_diagnosticLock)
            {
                _activeDiagnosticDirectories.Remove(diagnosticDirectory);
            }

            if (!exportCompleted)
            {
                TryDeleteDirectory(diagnosticDirectory);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _queue.Writer.TryComplete();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await _writerTask.ConfigureAwait(false);
    }

    internal bool TryWrite(
        MicrosoftLogLevel level,
        string category,
        EventId eventId,
        string message,
        Exception? exception)
    {
        if (Volatile.Read(ref _disposeState) != 0)
        {
            return false;
        }

        var safeException = exception == null
            ? string.Empty
            : _redactor.Redact(exception.ToString());
        var entry = new ApplicationLogRecord(
            _timeProvider.GetUtcNow().ToUniversalTime(),
            level,
            _redactor.Redact(category),
            eventId,
            _redactor.Redact(message),
            exception?.GetType().Name ?? string.Empty,
            Environment.ProcessId,
            Environment.CurrentManagedThreadId,
            CaptureScope(),
            safeException);

        AddRecent(entry);
        if (_queue.Writer.TryWrite(new EntryItem(entry)))
        {
            return true;
        }

        Interlocked.Increment(ref _droppedEntryCount);
        return false;
    }

    internal async Task RequestMaintenanceAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposeState) != 0)
        {
            await _writerTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _queue.Writer.WriteAsync(new MaintenanceItem(completion), cancellationToken).ConfigureAwait(false);
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private string CaptureScope()
    {
        var scopes = new List<string>();
        ScopeProvider.ForEachScope(static (scope, state) =>
        {
            var text = scope?.ToString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                state.Add(text);
            }
        }, scopes);
        return _redactor.Redact(string.Join(" => ", scopes));
    }

    private void AddRecent(ApplicationLogRecord entry)
    {
        lock (_recentLock)
        {
            _recentEvents.Enqueue(entry);
            while (_recentEvents.Count > _options.RecentEventCapacity)
            {
                _recentEvents.Dequeue();
            }
        }
    }

    private async Task ProcessQueueAsync()
    {
        StreamWriter? writer = null;
        string? currentPath = null;
        var currentLength = 0L;
        var nextMaintenanceUtc = _timeProvider.GetUtcNow().ToUniversalTime() + _options.MaintenanceInterval;
        try
        {
            Directory.CreateDirectory(LogDirectory);
            ApplyRetentionPolicy(activePath: null, _timeProvider.GetUtcNow().ToUniversalTime());
            await foreach (var item in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                switch (item)
                {
                    case FlushItem flush:
                        await CloseWriterAsync().ConfigureAwait(false);
                        flush.Completion.TrySetResult();
                        continue;
                    case MaintenanceItem maintenance:
                        ApplyRetentionPolicy(currentPath, _timeProvider.GetUtcNow().ToUniversalTime());
                        nextMaintenanceUtc = _timeProvider.GetUtcNow().ToUniversalTime() + _options.MaintenanceInterval;
                        maintenance.Completion.TrySetResult();
                        continue;
                }

                var entry = (EntryItem)item;
                var text = string.Concat(SerializeRecord(entry.Record), "\n");
                var byteCount = Encoding.UTF8.GetByteCount(text);
                var timestamp = entry.Record.Timestamp.ToUniversalTime();
                if (timestamp >= nextMaintenanceUtc)
                {
                    ApplyRetentionPolicy(currentPath, timestamp);
                    nextMaintenanceUtc = timestamp + _options.MaintenanceInterval;
                }

                if (writer == null
                    || currentPath == null
                    || !IsCurrentLogPath(currentPath, timestamp)
                    || currentLength + byteCount > _options.MaxFileBytes)
                {
                    var hadWriter = writer != null;
                    await CloseWriterAsync().ConfigureAwait(false);
                    if (hadWriter)
                    {
                        ApplyRetentionPolicy(activePath: null, timestamp);
                    }

                    currentPath = GetNextLogPath(timestamp, byteCount);
                    currentLength = File.Exists(currentPath) ? new FileInfo(currentPath).Length : 0;
                    var stream = new FileStream(
                        currentPath,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.ReadWrite,
                        64 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    writer = new StreamWriter(stream, new UTF8Encoding(false));
                }

                await writer.WriteAsync(text.AsMemory(), CancellationToken.None).ConfigureAwait(false);
                currentLength += byteCount;
                Interlocked.Add(ref _bytesWritten, byteCount);
                Interlocked.Add(ref _retainedBytes, byteCount);
                Interlocked.Increment(ref _eventsWritten);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _writerFailure = exception;
            _queue.Writer.TryComplete(exception);
        }
        finally
        {
            await CloseWriterAsync().ConfigureAwait(false);
            while (_queue.Reader.TryRead(out var item))
            {
                CompletePendingItem(item, _writerFailure);
            }
        }

        async ValueTask CloseWriterAsync()
        {
            if (writer != null)
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }

            writer = null;
            currentPath = null;
            currentLength = 0;
        }
    }

    private string GetNextLogPath(DateTimeOffset timestamp, int incomingBytes)
    {
        var dayDirectory = Path.Combine(
            LogDirectory,
            timestamp.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dayDirectory);
        var files = Directory.GetFiles(dayDirectory, EventFilePattern, SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length > 0)
        {
            var latest = files[^1];
            if (new FileInfo(latest).Length + incomingBytes <= _options.MaxFileBytes)
            {
                return latest;
            }
        }

        var nextIndex = files.Length == 0
            ? 0
            : ParseLogIndex(files[^1]) + 1;
        return Path.Combine(dayDirectory, $"events-{nextIndex:D3}.jsonl");
    }

    private static int ParseLogIndex(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        var separator = fileName.LastIndexOf('-');
        return separator >= 0
               && int.TryParse(fileName[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            ? index
            : 0;
    }

    private static bool IsCurrentLogPath(string path, DateTimeOffset timestamp)
    {
        return string.Equals(
            Directory.GetParent(path)?.Name,
            timestamp.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
    }

    private void ApplyRetentionPolicy(string? activePath, DateTimeOffset nowUtc)
    {
        var threshold = nowUtc.UtcDateTime - _options.MaxRetainedAge;
        var units = GetRetentionUnits();
        foreach (var unit in units.Where(unit => unit.LastWriteTimeUtc < threshold).ToArray())
        {
            if (IsActive(unit, activePath) || !TryDelete(unit))
            {
                continue;
            }

            units.Remove(unit);
            Interlocked.Increment(ref _ageDeletionCount);
        }

        var retainedBytes = units.Sum(static unit => unit.Length);
        foreach (var unit in units.ToArray())
        {
            if (retainedBytes <= _options.MaxTotalBytes)
            {
                break;
            }

            if (IsActive(unit, activePath) || !TryDelete(unit))
            {
                continue;
            }

            retainedBytes -= unit.Length;
            units.Remove(unit);
            Interlocked.Increment(ref _capacityDeletionCount);
        }

        RemoveEmptyDayDirectories();
        Interlocked.Exchange(ref _retainedBytes, Math.Max(0, retainedBytes));
        Interlocked.Exchange(ref _lastMaintenanceUtcTicks, nowUtc.UtcTicks);
    }

    private List<RetentionUnit> GetRetentionUnits()
    {
        var units = new List<RetentionUnit>();
        if (!Directory.Exists(LogDirectory))
        {
            return units;
        }

        try
        {
            units.AddRange(Directory.GetFiles(LogDirectory, EventFilePattern, SearchOption.AllDirectories)
                .Select(static path => new FileInfo(path))
                .Select(static file => new RetentionUnit(
                    file.FullName,
                    IsDirectory: false,
                    file.LastWriteTimeUtc,
                    file.Length)));

            var diagnosticsRoot = Path.Combine(LogDirectory, DiagnosticDirectoryName);
            if (Directory.Exists(diagnosticsRoot))
            {
                foreach (var directory in Directory.GetDirectories(
                             diagnosticsRoot,
                             "diagnostic-*",
                             SearchOption.TopDirectoryOnly))
                {
                    var files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
                        .Select(static path => new FileInfo(path))
                        .ToArray();
                    var lastWriteTimeUtc = files.Length == 0
                        ? new DirectoryInfo(directory).LastWriteTimeUtc
                        : files.Max(static file => file.LastWriteTimeUtc);
                    units.Add(new RetentionUnit(
                        Path.GetFullPath(directory),
                        IsDirectory: true,
                        lastWriteTimeUtc,
                        files.Sum(static file => file.Length)));
                }
            }
        }
        catch (IOException)
        {
            Interlocked.Increment(ref _maintenanceFailureCount);
        }
        catch (UnauthorizedAccessException)
        {
            Interlocked.Increment(ref _maintenanceFailureCount);
        }

        return units.OrderBy(static unit => unit.LastWriteTimeUtc).ToList();
    }

    private bool TryDelete(RetentionUnit unit)
    {
        if (unit.IsDirectory)
        {
            return TryDeleteDirectory(unit.Path);
        }

        try
        {
            File.Delete(unit.Path);
            return true;
        }
        catch (IOException)
        {
            Interlocked.Increment(ref _maintenanceFailureCount);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            Interlocked.Increment(ref _maintenanceFailureCount);
            return false;
        }
    }

    private bool TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }

            return true;
        }
        catch (IOException)
        {
            Interlocked.Increment(ref _maintenanceFailureCount);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            Interlocked.Increment(ref _maintenanceFailureCount);
            return false;
        }
    }

    private void RemoveEmptyDayDirectories()
    {
        foreach (var directory in Directory.GetDirectories(LogDirectory, "????-??-??", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
            catch (IOException)
            {
                Interlocked.Increment(ref _maintenanceFailureCount);
            }
            catch (UnauthorizedAccessException)
            {
                Interlocked.Increment(ref _maintenanceFailureCount);
            }
        }
    }

    private bool IsActive(RetentionUnit unit, string? activePath)
    {
        if (!unit.IsDirectory)
        {
            return activePath != null && PathsEqual(unit.Path, activePath);
        }

        lock (_diagnosticLock)
        {
            return _activeDiagnosticDirectories.Contains(unit.Path);
        }
    }

    private string ReserveDiagnosticDirectory(DateTimeOffset timestamp)
    {
        var diagnosticsRoot = Path.Combine(LogDirectory, DiagnosticDirectoryName);
        Directory.CreateDirectory(diagnosticsRoot);
        var stem = $"diagnostic-{timestamp:yyyyMMdd'T'HHmmssfff'Z'}";
        lock (_diagnosticLock)
        {
            for (var index = 0; index < 1000; index++)
            {
                var name = index == 0 ? stem : $"{stem}-{index:D3}";
                var candidate = Path.GetFullPath(Path.Combine(diagnosticsRoot, name));
                if (Directory.Exists(candidate))
                {
                    continue;
                }

                Directory.CreateDirectory(candidate);
                _activeDiagnosticDirectories.Add(candidate);
                return candidate;
            }
        }

        throw new IOException("Unable to reserve a unique diagnostics export directory.");
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static string SerializeRecord(ApplicationLogRecord record)
    {
        return JsonSerializer.Serialize(record, ApplicationLogJsonContext.Default.ApplicationLogRecord);
    }

    private static void CompletePendingItem(QueueItem item, Exception? failure)
    {
        var completion = item switch
        {
            FlushItem flush => flush.Completion,
            MaintenanceItem maintenance => maintenance.Completion,
            _ => null
        };
        if (completion == null)
        {
            return;
        }

        if (failure == null)
        {
            completion.TrySetResult();
        }
        else
        {
            completion.TrySetException(failure);
        }
    }

    private abstract record QueueItem;

    private sealed record EntryItem(ApplicationLogRecord Record) : QueueItem;

    private sealed record FlushItem(TaskCompletionSource Completion) : QueueItem;

    private sealed record MaintenanceItem(TaskCompletionSource Completion) : QueueItem;

    private sealed record RetentionUnit(
        string Path,
        bool IsDirectory,
        DateTime LastWriteTimeUtc,
        long Length);

    private sealed class ApplicationLogger(ApplicationLogProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return provider.ScopeProvider.Push(state);
        }

        public bool IsEnabled(MicrosoftLogLevel logLevel)
        {
            return logLevel != MicrosoftLogLevel.None;
        }

        public void Log<TState>(
            MicrosoftLogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (IsEnabled(logLevel))
            {
                provider.TryWrite(logLevel, category, eventId, formatter(state, exception), exception);
            }
        }
    }
}
