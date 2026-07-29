using DownKyi.Application.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DownKyi.Infrastructure.Logging;

internal sealed class ApplicationLogRecordFactory(
    ISensitiveDataRedactor redactor,
    TimeProvider timeProvider)
{
    public ApplicationLogRecord Create(
        LogLevel level,
        string category,
        EventId eventId,
        string message,
        Exception? exception,
        string scope)
    {
        return new ApplicationLogRecord(
            timeProvider.GetUtcNow().ToUniversalTime(),
            level,
            redactor.Redact(category),
            new EventId(eventId.Id, redactor.Redact(eventId.Name)),
            redactor.Redact(message),
            exception?.GetType().Name ?? string.Empty,
            Environment.ProcessId,
            Environment.CurrentManagedThreadId,
            redactor.Redact(scope),
            exception == null ? string.Empty : redactor.Redact(exception.ToString()));
    }
}
