using System.Runtime.ExceptionServices;
using DownKyi.Application.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DownKyi.Infrastructure.Logging;

public sealed class ApplicationLogProvider :
    ILoggerProvider,
    ISupportExternalScope,
    IApplicationLogService,
    IAsyncDisposable
{
    private readonly ApplicationLogOptions _options;
    private readonly ApplicationLogRecordFactory _recordFactory;
    private readonly ApplicationRecentLogBuffer _recentBuffer;
    private readonly NLogAsyncRollingFileSink _sink;
    private readonly ApplicationLogRetentionManager _retention;
    private readonly ApplicationLogRetentionWorker _retentionWorker;
    private readonly DiagnosticLogExporter _exporter;
    private readonly object _shutdownGate = new();
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();
    private Task? _shutdownTask;
    private int _disposeState;

    public ApplicationLogProvider(ApplicationLogOptions options)
        : this(
            options ?? throw new ArgumentNullException(nameof(options)),
            new SensitiveDataRedactor(),
            TimeProvider.System)
    {
    }

    internal ApplicationLogProvider(
        ApplicationLogOptions options,
        ISensitiveDataRedactor redactor,
        TimeProvider timeProvider)
    {
        ValidateOptions(options);
        _options = options with { LogDirectory = Path.GetFullPath(options.LogDirectory) };
        _recordFactory = new ApplicationLogRecordFactory(redactor, timeProvider);
        _recentBuffer = new ApplicationRecentLogBuffer(_options.RecentEventCapacity);
        _sink = new NLogAsyncRollingFileSink(_options);
        _retention = new ApplicationLogRetentionManager(_options);
        _retentionWorker = new ApplicationLogRetentionWorker(
            _retention,
            _sink.GetActiveFilePath,
            timeProvider,
            _options.MaintenanceInterval);
        _exporter = new DiagnosticLogExporter(_options, redactor, _retention, timeProvider);
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
        return _recentBuffer.Snapshot();
    }

    public ApplicationLogMetrics GetMetrics()
    {
        var sink = _sink.GetMetrics();
        var retention = _retention.GetMetrics();
        return new ApplicationLogMetrics(
            sink.BytesWritten,
            sink.EventsWritten,
            sink.DroppedEvents,
            retention.AgeDeletionCount,
            retention.CapacityDeletionCount,
            retention.MaintenanceFailureCount,
            retention.RetainedBytes,
            (double)retention.RetainedBytes / _options.MaxTotalBytes,
            retention.LastMaintenanceUtc,
            retention.MalformedExportRecords);
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        var shutdown = Volatile.Read(ref _shutdownTask);
        if (shutdown != null)
        {
            await shutdown.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await _retentionWorker.Startup.WaitAsync(cancellationToken).ConfigureAwait(false);
        await _sink.FlushAsync(cancellationToken).ConfigureAwait(false);
        await _retentionWorker.RunMaintenanceAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ExportDiagnosticLogAsync(CancellationToken cancellationToken = default)
    {
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        return await _exporter.ExportAsync(GetMetrics, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _ = GetOrStartShutdown();
    }

    public async ValueTask DisposeAsync()
    {
        await GetOrStartShutdown().ConfigureAwait(false);
    }

    internal bool TryWrite(
        LogLevel level,
        string category,
        EventId eventId,
        string message,
        Exception? exception)
    {
        if (Volatile.Read(ref _disposeState) != 0)
        {
            return false;
        }

        var record = _recordFactory.Create(
            level,
            category,
            eventId,
            message,
            exception,
            CaptureScope());
        _recentBuffer.Add(record);
        return _sink.TryWrite(record);
    }

    internal async Task RequestMaintenanceAsync(CancellationToken cancellationToken)
    {
        await _sink.FlushAsync(cancellationToken).ConfigureAwait(false);
        await _retentionWorker.RunMaintenanceAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateOptions(ApplicationLogOptions? options)
    {
        ArgumentNullException.ThrowIfNull(options);
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
        return string.Join(" => ", scopes);
    }

    private Task GetOrStartShutdown()
    {
        lock (_shutdownGate)
        {
            if (_shutdownTask != null)
            {
                return _shutdownTask;
            }

            Interlocked.Exchange(ref _disposeState, 1);
            _shutdownTask = ShutdownAsync();
            return _shutdownTask;
        }
    }

    private async Task ShutdownAsync()
    {
        Exception? failure = null;
        try
        {
            await _retentionWorker.Startup.ConfigureAwait(false);
            await _sink.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            await _retentionWorker.RunMaintenanceAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or NLog.NLogRuntimeException)
        {
            failure = exception;
        }

        try
        {
            await _retentionWorker.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or NLog.NLogRuntimeException)
        {
            failure ??= exception;
        }

        try
        {
            await _sink.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or NLog.NLogRuntimeException)
        {
            failure ??= exception;
        }

        if (failure != null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private sealed class ApplicationLogger(ApplicationLogProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return provider.ScopeProvider.Push(state);
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel != LogLevel.None;
        }

        public void Log<TState>(
            LogLevel logLevel,
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
