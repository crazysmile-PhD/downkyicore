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
    ApplicationLogMetrics Storage);

internal sealed record FeedbackDiagnosticManifest(
    int SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    string ApplicationVersion,
    string Runtime,
    string OperatingSystem,
    string Architecture,
    int EventCount,
    string[] Files);

internal sealed record FeedbackDiagnosticEvent(
    DateTimeOffset Timestamp,
    string Level,
    string Component,
    int EventId,
    string ExceptionDiagnostic);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false)]
[JsonSerializable(typeof(ApplicationLogRecord))]
[JsonSerializable(typeof(ApplicationDiagnosticManifest))]
[JsonSerializable(typeof(FeedbackDiagnosticManifest))]
[JsonSerializable(typeof(FeedbackDiagnosticEvent))]
internal sealed partial class ApplicationLogJsonContext : JsonSerializerContext;
