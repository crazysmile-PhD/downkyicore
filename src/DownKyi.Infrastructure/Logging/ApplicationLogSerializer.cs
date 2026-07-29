using System.Text.Json;
using DownKyi.Application.Diagnostics;

namespace DownKyi.Infrastructure.Logging;

internal static class ApplicationLogSerializer
{
    public static string Serialize(ApplicationLogRecord record)
    {
        return JsonSerializer.Serialize(record, ApplicationLogJsonContext.Default.ApplicationLogRecord);
    }

    public static ApplicationLogRecord? Deserialize(string json)
    {
        return JsonSerializer.Deserialize(json, ApplicationLogJsonContext.Default.ApplicationLogRecord);
    }
}
