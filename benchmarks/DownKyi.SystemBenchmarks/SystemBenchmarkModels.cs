using System.Text.Json.Serialization;

namespace DownKyi.SystemBenchmarks;

internal sealed record SystemBenchmarkReport(
    int SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    BenchmarkEnvironment Environment,
    IReadOnlyList<SystemBenchmarkResult> Results);

internal sealed record BenchmarkEnvironment(
    string Runtime,
    string OperatingSystem,
    string Architecture,
    string CommitSha);

internal sealed record SystemBenchmarkResult(
    string Name,
    string Dataset,
    string DownloaderBackend,
    bool Available,
    IReadOnlyDictionary<string, double> Metrics,
    IReadOnlyDictionary<string, string> Units,
    string Notes);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(SystemBenchmarkReport))]
internal sealed partial class SystemBenchmarkJsonContext : JsonSerializerContext;
