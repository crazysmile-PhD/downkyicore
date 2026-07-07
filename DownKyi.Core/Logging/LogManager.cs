using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using DownKyi.Core.Storage;

namespace DownKyi.Core.Logging;

/// <summary>
/// 日志组件
/// </summary>
public class LogManager
{
    private const long MaxLogFileBytes = 1 * 1024 * 1024;
    private static readonly ConcurrentQueue<Tuple<string, string>> LogQueue = new();
    private static readonly CancellationTokenSource WriterCancellation = new();
    private static readonly object WriterLock = new();
    private static readonly object LogPathLock = new();
    private static readonly Task WriterTask;
    private static string? CachedLogDate;
    private static string? CachedLogPath;

    /// <summary>
    /// 自定义事件
    /// </summary>
    public static event Action<LogInfo>? Event;

    static LogManager()
    {
        WriterTask = Task.Factory.StartNew(
            () => ProcessQueueAsync(WriterCancellation.Token),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// 日志存放目录，windows默认日志放在当前应用程序运行目录下的Logs文件夹中,macOS、linux存放于applicationData目录下
    /// </summary>
    private static string LogDirectory => StorageManager.GetLogsDir();

    public static Task FlushAsync(TimeSpan timeout)
    {
        var flushTask = Task.Run(DrainQueue);
        return Task.WhenAny(flushTask, Task.Delay(timeout));
    }

    public static void Flush(TimeSpan timeout)
    {
        try
        {
            FlushAsync(timeout).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            System.Diagnostics.Debug.WriteLine(e);
        }
    }

    /// <summary>
    /// 写入Info级别的日志
    /// </summary>
    public static void Info(string info)
    {
        Write(LogLevel.Info, null, info);
    }

    /// <summary>
    /// 写入Info级别的日志
    /// </summary>
    public static void Info(string source, string info)
    {
        Write(LogLevel.Info, source, info);
    }

    /// <summary>
    /// 写入Info级别的日志
    /// </summary>
    public static void Info(Type source, string info)
    {
        Write(LogLevel.Info, source.FullName, info);
    }

    /// <summary>
    /// 写入debug级别日志
    /// </summary>
    [Conditional("DEBUG")]
    public static void Debug(string debug)
    {
        Write(LogLevel.Debug, null, debug);
    }

    /// <summary>
    /// 写入debug级别日志
    /// </summary>
    [Conditional("DEBUG")]
    public static void Debug(string source, string debug)
    {
        Write(LogLevel.Debug, source, debug);
    }

    /// <summary>
    /// 写入debug级别日志
    /// </summary>
    [Conditional("DEBUG")]
    public static void Debug(Type source, string debug)
    {
        Write(LogLevel.Debug, source.FullName, debug);
    }

    /// <summary>
    /// 写入error级别日志
    /// </summary>
    public static void Error(Exception error)
    {
        Write(LogLevel.Error, error.Source, error.Message, error);
    }

    /// <summary>
    /// 写入error级别日志
    /// </summary>
    public static void Error(Type source, Exception error)
    {
        Write(LogLevel.Error, source.FullName, error.Message, error);
    }

    /// <summary>
    /// 写入error级别日志
    /// </summary>
    public static void Error(Type source, string error)
    {
        Write(LogLevel.Error, source.FullName, error);
    }

    /// <summary>
    /// 写入error级别日志
    /// </summary>
    public static void Error(string source, Exception error)
    {
        Write(LogLevel.Error, source, error.Message, error);
    }

    /// <summary>
    /// 写入error级别日志
    /// </summary>
    public static void Error(string source, string error)
    {
        Write(LogLevel.Error, source, error);
    }

    /// <summary>
    /// 写入fatal级别日志
    /// </summary>
    public static void Fatal(Exception fatal)
    {
        Write(LogLevel.Fatal, fatal.Source, fatal.Message, fatal);
    }

    /// <summary>
    /// 写入fatal级别日志
    /// </summary>
    public static void Fatal(Type source, Exception fatal)
    {
        Write(LogLevel.Fatal, source.FullName, fatal.Message, fatal);
    }

    /// <summary>
    /// 写入fatal级别日志
    /// </summary>
    public static void Fatal(Type source, string fatal)
    {
        Write(LogLevel.Fatal, source.FullName, fatal);
    }

    /// <summary>
    /// 写入fatal级别日志
    /// </summary>
    public static void Fatal(string source, Exception fatal)
    {
        Write(LogLevel.Fatal, source, fatal.Message, fatal);
    }

    /// <summary>
    /// 写入fatal级别日志
    /// </summary>
    public static void Fatal(string source, string fatal)
    {
        Write(LogLevel.Fatal, source, fatal);
    }

    private static async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                DrainQueue();
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            DrainQueue();
        }
    }

    private static void Write(LogLevel level, string? source, string message, Exception? exception = null)
    {
        var now = DateTime.Now;
        var sourceText = string.IsNullOrWhiteSpace(source) ? string.Empty : $"{source}  ";
        var stack = exception == null ? string.Empty : $"{Environment.NewLine}{exception.StackTrace}";
        var logText =
            $"{now}   [{Environment.CurrentManagedThreadId}]   {level.ToString().ToUpperInvariant()}   {sourceText}{message}{stack}";

        LogQueue.Enqueue(new Tuple<string, string>(GetLogPath(), logText));

        Event?.Invoke(new LogInfo
        {
            LogLevel = level,
            Message = message,
            Time = now,
            ThreadId = Environment.CurrentManagedThreadId,
            Source = source ?? string.Empty,
            Exception = exception!,
            ExceptionType = exception?.GetType().Name ?? string.Empty,
            RequestUrl = string.Empty,
            UserAgent = string.Empty
        });
    }

    private static void DrainQueue()
    {
        lock (WriterLock)
        {
            var batches = new Dictionary<string, StringBuilder>(StringComparer.OrdinalIgnoreCase);
            while (LogQueue.TryDequeue(out var logItem))
            {
                if (!batches.TryGetValue(logItem.Item1, out var batch))
                {
                    batch = new StringBuilder();
                    batches.Add(logItem.Item1, batch);
                }

                batch.AppendLine(logItem.Item2);
                batch.AppendLine("----------------------------------------------------------------------------------------------------------------------");
            }

            foreach (var item in batches)
            {
                WriteText(item.Key, item.Value.ToString());
            }
        }
    }

    private static string GetLogPath()
    {
        lock (LogPathLock)
        {
            var logDir = string.IsNullOrEmpty(LogDirectory)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs")
                : LogDirectory;
            Directory.CreateDirectory(logDir);

            var date = DateTime.Now.ToString("yyyyMMdd");
            if (CachedLogDate == date &&
                !string.IsNullOrEmpty(CachedLogPath) &&
                (!File.Exists(CachedLogPath) || new FileInfo(CachedLogPath).Length <= MaxLogFileBytes))
            {
                return CachedLogPath;
            }

            const string extension = ".log";
            var fileNamePattern = string.Concat(date, "(*)", extension);
            var filePaths = Directory.GetFiles(logDir, fileNamePattern, SearchOption.TopDirectoryOnly).ToList();

            string logPath;
            if (filePaths.Count > 0)
            {
                var fileMaxLen = filePaths.Max(d => d.Length);
                var lastFilePath = filePaths.Where(d => d.Length == fileMaxLen).OrderByDescending(d => d).FirstOrDefault();
                if (lastFilePath != null && new FileInfo(lastFilePath).Length > MaxLogFileBytes)
                {
                    var no = new Regex(@"(?is)(?<=\()(.*)(?=\))").Match(Path.GetFileName(lastFilePath)).Value;
                    var parse = int.TryParse(no, out var tempNo);
                    var formatNo = $"({(parse ? tempNo + 1 : tempNo)})";
                    logPath = Path.Combine(logDir, string.Concat(date, formatNo, extension));
                }
                else
                {
                    logPath = lastFilePath ?? Path.Combine(logDir, string.Concat(date, "(0)", extension));
                }
            }
            else
            {
                logPath = Path.Combine(logDir, string.Concat(date, "(0)", extension));
            }

            CachedLogDate = date;
            CachedLogPath = logPath;
            return logPath;
        }
    }

    private static void WriteText(string logPath, string logContent)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath) ?? AppDomain.CurrentDomain.BaseDirectory);
            File.AppendAllText(logPath, logContent, Encoding.UTF8);
        }
        catch (Exception e)
        {
            System.Diagnostics.Debug.WriteLine(e);
        }
    }
}
