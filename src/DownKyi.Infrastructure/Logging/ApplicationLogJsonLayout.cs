using System.Text;
using DownKyi.Application.Diagnostics;
using NLog;
using NLog.Config;
using NLog.Layouts;

namespace DownKyi.Infrastructure.Logging;

[ThreadAgnostic]
internal sealed class ApplicationLogJsonLayout(Action<int> recordRendered) : Layout
{
    protected override string GetFormattedMessage(LogEventInfo logEvent)
    {
        if (logEvent.Parameters is not [ApplicationLogRecord record])
        {
            throw new NLogRuntimeException("The application log event is missing its redacted record.");
        }

        var json = ApplicationLogSerializer.Serialize(record);
        recordRendered(Encoding.UTF8.GetByteCount(json) + 1);
        return json;
    }
}
