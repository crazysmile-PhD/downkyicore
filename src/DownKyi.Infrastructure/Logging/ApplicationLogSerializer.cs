using System.Text.Json;
using System.Text.Json.Serialization;
using DownKyi.Application.Diagnostics;
using Microsoft.Extensions.Logging;

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

    // Adapt the readonly framework type without changing the existing persisted JSON shape.
    internal readonly record struct PersistedEventId(int Id, string? Name);

    internal sealed class EventIdConverter : JsonConverter<EventId>
    {
        public override EventId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var stored = JsonSerializer.Deserialize(ref reader, ApplicationLogJsonContext.Default.PersistedEventId);
            return new EventId(stored.Id, stored.Name);
        }

        public override void Write(Utf8JsonWriter writer, EventId value, JsonSerializerOptions options)
        {
            JsonSerializer.Serialize(writer, new PersistedEventId(value.Id, value.Name),
                ApplicationLogJsonContext.Default.PersistedEventId);
        }
    }
}
