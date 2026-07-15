using Microsoft.Extensions.Logging;
using MicrosoftLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace DownKyi.Core.Logging;

public static class ApplicationLoggerExtensions
{
    private static readonly Action<ILogger, string, Exception?> DebugMessage = LoggerMessage.Define<string>(
        MicrosoftLogLevel.Debug,
        new EventId(1000, nameof(DebugMessage)),
        "{Message}");
    private static readonly Action<ILogger, string, Exception?> InformationMessage = LoggerMessage.Define<string>(
        MicrosoftLogLevel.Information,
        new EventId(1001, nameof(InformationMessage)),
        "{Message}");
    private static readonly Action<ILogger, string, Exception?> WarningMessage = LoggerMessage.Define<string>(
        MicrosoftLogLevel.Warning,
        new EventId(1002, nameof(WarningMessage)),
        "{Message}");
    private static readonly Action<ILogger, string, Exception?> ErrorMessage = LoggerMessage.Define<string>(
        MicrosoftLogLevel.Error,
        new EventId(1003, nameof(ErrorMessage)),
        "{Message}");
    private static readonly Action<ILogger, string, Exception?> CriticalMessage = LoggerMessage.Define<string>(
        MicrosoftLogLevel.Critical,
        new EventId(1004, nameof(CriticalMessage)),
        "{Message}");
    private static readonly Func<ILogger, string, string, int, IDisposable?> OperationScope =
        LoggerMessage.DefineScope<string, string, int>(
            "CorrelationId={CorrelationId} DownloadTask={DownloadTask} ChildProcess={ChildProcess}");

    public static void LogDebugMessage(this ILogger logger, string message)
    {
        ArgumentNullException.ThrowIfNull(logger);
        DebugMessage(logger, message, null);
    }

    public static void LogInformationMessage(this ILogger logger, string message)
    {
        ArgumentNullException.ThrowIfNull(logger);
        InformationMessage(logger, message, null);
    }

    public static void LogWarningMessage(this ILogger logger, string message)
    {
        ArgumentNullException.ThrowIfNull(logger);
        WarningMessage(logger, message, null);
    }

    public static void LogErrorMessage(this ILogger logger, string message, Exception? exception = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ErrorMessage(logger, message, exception);
    }

    public static void LogCriticalMessage(this ILogger logger, string message, Exception? exception = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        CriticalMessage(logger, message, exception);
    }

    public static IDisposable? BeginOperationScope(
        this ILogger logger,
        string correlationId,
        string downloadTask = "",
        int childProcess = 0)
    {
        ArgumentNullException.ThrowIfNull(logger);
        return OperationScope(logger, correlationId, downloadTask, childProcess);
    }
}
