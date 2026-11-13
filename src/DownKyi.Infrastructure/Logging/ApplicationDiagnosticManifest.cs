using System.Text.Json.Serialization;
using DownKyi.Application.Diagnostics;

namespace DownKyi.Infrastructure.Logging;

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
    ApplicationLogMetrics Storage,
    bool Truncated);

internal sealed record FeedbackDiagnosticManifest(
    int SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    string ApplicationVersion,
    string Runtime,
    string OperatingSystem,
    string Architecture,
    int EventCount,
    string[] Files,
    bool Truncated);

internal sealed record FeedbackDiagnosticEvent(
    DateTimeOffset Timestamp,
    string Level,
    string Component,
    int EventId,
    string Message,
    string ExceptionDiagnostic,
    string ExceptionText);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    Converters = new[] { typeof(ApplicationLogSerializer.EventIdConverter) })]
[JsonSerializable(typeof(ApplicationLogRecord))]
[JsonSerializable(typeof(ApplicationLogSerializer.PersistedEventId))]
[JsonSerializable(typeof(ApplicationDiagnosticManifest))]
[JsonSerializable(typeof(FeedbackDiagnosticManifest))]
[JsonSerializable(typeof(FeedbackDiagnosticEvent))]
internal sealed partial class ApplicationLogJsonContext : JsonSerializerContext;
