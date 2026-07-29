using System.Globalization;
using System.Text;
using DownKyi.Application.Diagnostics;
using NLog;
using NLog.Common;
using NLog.Config;
using NLog.Layouts;
using NLog.Targets;
using NLog.Targets.Wrappers;

namespace DownKyi.Infrastructure.Logging;

internal sealed class NLogAsyncRollingFileSink : IAsyncDisposable
{
    private readonly ApplicationLogOptions _options;
    private readonly LogFactory _logFactory;
    private readonly LoggingConfiguration _configuration;
    private readonly Logger _logger;
    private readonly ReopenableFileTarget _fileTarget;
    private readonly AsyncTargetWrapper _asyncTarget;
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly object _writeGate = new();
    private readonly Queue<LogEventInfo> _deferredEvents = new();
    private readonly IOException? _initializationFailure;
    private long _submittedEvents;
    private long _renderedBytes;
    private long _droppedEvents;
    private bool _deferWrites;
    private int _disposeState;

    public NLogAsyncRollingFileSink(ApplicationLogOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _initializationFailure = File.Exists(_options.LogDirectory)
            ? new IOException("The configured log directory is an existing file.")
            : null;
        _logFactory = new LogFactory
        {
            AutoShutdown = false,
            ThrowExceptions = true,
            ThrowConfigExceptions = true
        };
        _configuration = new LoggingConfiguration(_logFactory);
        _fileTarget = new ReopenableFileTarget("downkyi-application-jsonl")
        {
            FileName = Path.Combine(
                _options.LogDirectory,
                "${date:format=yyyy-MM-dd:universalTime=true}",
                "events.jsonl"),
            Layout = new ApplicationLogJsonLayout(RecordRendered),
            ArchiveAboveSize = _options.MaxFileBytes,
            ArchiveSuffixFormat = "-{0:000}",
            ArchiveOldFileOnStartup = false,
            MaxArchiveDays = 0,
            MaxArchiveFiles = -1,
            KeepFileOpen = true,
            AutoFlush = false,
            Encoding = new UTF8Encoding(false)
        };
        _asyncTarget = new AsyncTargetWrapper(
            _fileTarget,
            _options.QueueCapacity,
            AsyncTargetWrapperOverflowAction.Discard)
        {
            BatchSize = Math.Min(200, _options.QueueCapacity),
            TimeToSleepBetweenBatches = 0,
            ForceLockingQueue = true
        };
        _asyncTarget.LogEventDropped += OnLogEventDropped;
        _configuration.AddRuleForAllLevels(_asyncTarget);
        _logFactory.Configuration = _configuration;
        _logger = _logFactory.GetLogger("DownKyi.Application");
    }

    public bool TryWrite(ApplicationLogRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (Volatile.Read(ref _disposeState) != 0)
        {
            return false;
        }

        if (_initializationFailure != null)
        {
            Interlocked.Increment(ref _droppedEvents);
            return false;
        }

        var logEvent = new LogEventInfo(NLog.LogLevel.Info, _logger.Name, string.Empty)
        {
            Parameters = [record],
            TimeStamp = record.Timestamp.UtcDateTime
        };
        lock (_writeGate)
        {
            if (Volatile.Read(ref _disposeState) != 0)
            {
                return false;
            }

            Interlocked.Increment(ref _submittedEvents);
            if (!_deferWrites)
            {
                _logger.Log(logEvent);
                return true;
            }

            if (_deferredEvents.Count >= _options.QueueCapacity)
            {
                RecordDropped();
                return false;
            }

            _deferredEvents.Enqueue(logEvent);
            return true;
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (_initializationFailure != null)
        {
            throw _initializationFailure;
        }

        await _flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            BeginDeferredWrites();
            try
            {
                while (true)
                {
                    await _logFactory.FlushAsync(cancellationToken).ConfigureAwait(false);
                    if (TryReleaseFileAndResumeWrites())
                    {
                        break;
                    }
                }
            }
            catch
            {
                ResumeWritesWithoutReleasingFile();
                throw;
            }
        }
        finally
        {
            _flushGate.Release();
        }
    }

    public string GetActiveFilePath(DateTimeOffset timestamp)
    {
        return Path.Combine(
            _options.LogDirectory,
            timestamp.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "events.jsonl");
    }

    public NLogSinkMetrics GetMetrics()
    {
        return new NLogSinkMetrics(
            Interlocked.Read(ref _renderedBytes),
            Math.Max(0, Interlocked.Read(ref _submittedEvents) - Interlocked.Read(ref _droppedEvents)),
            Interlocked.Read(ref _droppedEvents));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        try
        {
            await _flushGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (_initializationFailure == null)
                {
                    await _logFactory.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                _flushGate.Release();
            }
        }
        finally
        {
            _asyncTarget.LogEventDropped -= OnLogEventDropped;
            _logFactory.Shutdown();
            _logFactory.Dispose();
            _asyncTarget.Dispose();
            _fileTarget.Dispose();
            _flushGate.Dispose();
        }
    }

    private void OnLogEventDropped(object? sender, LogEventDroppedEventArgs eventArgs)
    {
        RecordDropped();
    }

    private void BeginDeferredWrites()
    {
        lock (_writeGate)
        {
            _deferWrites = true;
        }
    }

    private bool TryReleaseFileAndResumeWrites()
    {
        lock (_writeGate)
        {
            if (_deferredEvents.Count > 0)
            {
                while (_deferredEvents.TryDequeue(out var logEvent))
                {
                    _logger.Log(logEvent);
                }

                return false;
            }

            _fileTarget.ResetFileHandles();
            _deferWrites = false;
            return true;
        }
    }

    private void ResumeWritesWithoutReleasingFile()
    {
        lock (_writeGate)
        {
            while (_deferredEvents.TryDequeue(out var logEvent))
            {
                _logger.Log(logEvent);
            }

            _deferWrites = false;
        }
    }

    private void RecordRendered(int byteCount)
    {
        Interlocked.Add(ref _renderedBytes, byteCount);
    }

    private void RecordDropped()
    {
        Interlocked.Increment(ref _droppedEvents);
    }

    private sealed class ReopenableFileTarget(string name) : FileTarget(name)
    {
        protected override void Write(IList<AsyncLogEventInfo> logEvents)
        {
            foreach (var logEvent in logEvents)
            {
                Write(logEvent);
            }
        }

        public void ResetFileHandles()
        {
            CloseTarget();
            InitializeTarget();
        }
    }
}

internal sealed record NLogSinkMetrics(
    long BytesWritten,
    long EventsWritten,
    long DroppedEvents);
