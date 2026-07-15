using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using MicrosoftLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace DownKyi.Core.Logging;

public sealed class ApplicationLogProvider : ILoggerProvider, ISupportExternalScope, IApplicationLogService, IAsyncDisposable
{
    private const string Separator = "----------------------------------------------------------------------------------------------------------------------";
    private readonly ApplicationLogOptions _options;
    private readonly ISensitiveDataRedactor _redactor;
    private readonly Channel<QueueItem> _queue;
    private readonly Queue<ApplicationLogRecord> _recentEvents;
    private readonly object _recentLock = new();
    private readonly Task _writerTask;
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();
    private Exception? _writerFailure;
    private int _disposeState;
    private long _droppedEntryCount;

    public ApplicationLogProvider(ApplicationLogOptions options)
        : this(options, new SensitiveDataRedactor())
    {
    }

    internal ApplicationLogProvider(ApplicationLogOptions options, ISensitiveDataRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(options);
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        if (string.IsNullOrWhiteSpace(options.LogDirectory))
        {
            throw new ArgumentException("A log directory is required.", nameof(options));
        }

        if (options.QueueCapacity <= 0 || options.RecentEventCapacity <= 0 || options.MaxFileBytes <= 0
            || options.MaxRetainedFiles <= 0 || options.MaxRetainedAge <= TimeSpan.Zero)
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
        var diagnosticDirectory = Path.Combine(LogDirectory, "Diagnostics");
        Directory.CreateDirectory(diagnosticDirectory);

        var outputPath = Path.Combine(diagnosticDirectory, $"diagnostic-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");
        var builder = new StringBuilder();
        builder.AppendLine("DownKyi diagnostic log");
        builder.AppendLine(CultureInfo.InvariantCulture, $"GeneratedAt: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"OS: {_redactor.Redact(RuntimeInformation.OSDescription)}");
        builder.AppendLine(CultureInfo.InvariantCulture, $".NET: {Environment.Version}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Architecture: {RuntimeInformation.ProcessArchitecture}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"DroppedEntries: {Interlocked.Read(ref _droppedEntryCount)}");
        builder.AppendLine("Paths: redacted");
        builder.AppendLine();
        builder.AppendLine("Recent entries");
        builder.AppendLine(Separator);

        foreach (var entry in GetRecentEvents())
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.AppendLine(FormatEntry(entry));
            builder.AppendLine(Separator);
        }

        await File.WriteAllTextAsync(outputPath, builder.ToString(), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        return outputPath;
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

        var safeMessage = _redactor.Redact(message);
        var safeException = exception == null
            ? string.Empty
            : _redactor.Redact(exception.ToString());
        var scope = CaptureScope();
        var entry = new ApplicationLogRecord(
            DateTimeOffset.Now,
            level,
            _redactor.Redact(category),
            eventId,
            safeMessage,
            exception?.GetType().Name ?? string.Empty,
            Environment.ProcessId,
            Environment.CurrentManagedThreadId,
            scope);

        AddRecent(entry);
        if (_queue.Writer.TryWrite(new EntryItem(entry, safeException)))
        {
            return true;
        }

        Interlocked.Increment(ref _droppedEntryCount);
        return false;
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
        try
        {
            Directory.CreateDirectory(LogDirectory);
            ApplyRetentionPolicy();
            await foreach (var item in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (item is FlushItem flush)
                {
                    if (writer != null)
                    {
                        await writer.DisposeAsync().ConfigureAwait(false);
                        writer = null;
                        currentPath = null;
                        currentLength = 0;
                    }

                    flush.Completion.TrySetResult();
                    continue;
                }

                var entry = (EntryItem)item;
                var text = string.Concat(FormatEntry(entry.Record), Environment.NewLine,
                    entry.ExceptionText.Length == 0 ? string.Empty : string.Concat(entry.ExceptionText, Environment.NewLine),
                    Separator, Environment.NewLine);
                var byteCount = Encoding.UTF8.GetByteCount(text);
                if (writer == null || currentPath == null || !IsCurrentLogPath(currentPath, entry.Record.Timestamp)
                    || currentLength + byteCount > _options.MaxFileBytes)
                {
                    if (writer != null)
                    {
                        await writer.DisposeAsync().ConfigureAwait(false);
                    }

                    currentPath = GetNextLogPath(entry.Record.Timestamp, byteCount);
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
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _writerFailure = exception;
            _queue.Writer.TryComplete(exception);
            Debug.WriteLine(exception);
        }
        finally
        {
            if (writer != null)
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }

            while (_queue.Reader.TryRead(out var item))
            {
                if (item is FlushItem flush)
                {
                    if (_writerFailure == null)
                    {
                        flush.Completion.TrySetResult();
                    }
                    else
                    {
                        flush.Completion.TrySetException(_writerFailure);
                    }
                }
            }
        }
    }

    private string GetNextLogPath(DateTimeOffset timestamp, int incomingBytes)
    {
        var prefix = timestamp.LocalDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var files = Directory.GetFiles(LogDirectory, $"{prefix}-*.log", SearchOption.TopDirectoryOnly)
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
        return Path.Combine(LogDirectory, $"{prefix}-{nextIndex:D3}.log");
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
        return Path.GetFileName(path).StartsWith(
            timestamp.LocalDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
    }

    private void ApplyRetentionPolicy()
    {
        var threshold = DateTime.UtcNow - _options.MaxRetainedAge;
        var files = Directory.GetFiles(LogDirectory, "*.log", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToArray();
        for (var index = 0; index < files.Length; index++)
        {
            if (index < _options.MaxRetainedFiles && files[index].LastWriteTimeUtc >= threshold)
            {
                continue;
            }

            try
            {
                files[index].Delete();
            }
            catch (IOException exception)
            {
                Debug.WriteLine(exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                Debug.WriteLine(exception);
            }
        }
    }

    private static string FormatEntry(ApplicationLogRecord entry)
    {
        var scope = string.IsNullOrEmpty(entry.Scope) ? string.Empty : $" scope={entry.Scope}";
        var eventId = entry.EventId.Id == 0 ? string.Empty : $" event={entry.EventId.Id}";
        return FormattableString.Invariant(
            $"{entry.Timestamp:O} [{entry.ProcessId}:{entry.ThreadId}] {entry.Level.ToString().ToUpperInvariant()} {entry.Category}{eventId}{scope} {entry.Message}");
    }

    private abstract record QueueItem;

    private sealed record EntryItem(ApplicationLogRecord Record, string ExceptionText) : QueueItem;

    private sealed record FlushItem(TaskCompletionSource Completion) : QueueItem;

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
