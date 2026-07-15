using Microsoft.Extensions.Logging;
using MicrosoftLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace DownKyi.Core.Logging;

public sealed record ApplicationLogRecord(
    DateTimeOffset Timestamp,
    MicrosoftLogLevel Level,
    string Category,
    EventId EventId,
    string Message,
    string ExceptionType,
    int ProcessId,
    int ThreadId,
    string Scope);
