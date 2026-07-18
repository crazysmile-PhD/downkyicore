using System.Text.Json.Serialization;

namespace DownKyi.Core.Logging;

internal sealed record ApplicationDiagnosticManifest(
    int SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    string ApplicationVersion,
    string Runtime,
    string OperatingSystem,
    string Architecture,
    int EventCount,
    string[] Files,
    string[] Redaction,
    ApplicationLogMetrics Storage);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false)]
[JsonSerializable(typeof(ApplicationLogRecord))]
[JsonSerializable(typeof(ApplicationDiagnosticManifest))]
internal sealed partial class ApplicationLogJsonContext : JsonSerializerContext;
