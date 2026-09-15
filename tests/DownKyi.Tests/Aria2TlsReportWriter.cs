using System.Text.Json;

namespace DownKyi.Tests;

internal sealed record Aria2TlsReportContext(
    string Runtime,
    string OperatingSystem,
    string Architecture,
    string RuntimeIdentifier,
    string AssetRuntimeIdentifier,
    string CommitSha,
    string AriaVersion,
    string BinarySha256,
    string RequiredFeature,
    string TlsBackend,
    string CertificateAuthoritySource);

internal static class Aria2TlsReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly IReadOnlyList<string> ForbiddenTerms =
    [
        "test-session=fixture",
        "Bearer fixture",
        "Basic Zml4dHVyZQ==",
        "X-Access-Token: fixture",
        "X-API-Key: fixture",
        "sessdata",
        "bili_jct",
        "dedeuserid",
        "http://",
        "https://",
        "C:\\Users\\",
        "/Users/",
        "/home/"
    ];

    public static Aria2TlsReport Build(
        int expectedCaseCount,
        IReadOnlyCollection<Aria2TlsCaseResult> cases,
        Aria2TlsReportContext context)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedCaseCount);
        ArgumentNullException.ThrowIfNull(cases);
        ArgumentNullException.ThrowIfNull(context);

        var complete = cases.Count == expectedCaseCount;
        return new Aria2TlsReport(
            SchemaVersion: 2,
            Complete: complete,
            Passed: complete && cases.All(result => result.Passed),
            context.Runtime,
            context.OperatingSystem,
            context.Architecture,
            context.RuntimeIdentifier,
            context.AssetRuntimeIdentifier,
            context.CommitSha,
            context.AriaVersion,
            context.BinarySha256,
            context.RequiredFeature,
            context.TlsBackend,
            context.CertificateAuthoritySource,
            Cases: cases);
    }

    public static string Serialize(Aria2TlsReport report)
    {
        return Serialize(
            report,
            value => JsonSerializer.Serialize(value, JsonOptions));
    }

    internal static string Serialize(
        Aria2TlsReport report,
        Func<Aria2TlsReport, string> serialize)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(serialize);
        return serialize(report);
    }

    public static string EnsureSanitized(string reportJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportJson);
        if (ForbiddenTerms.Any(term => reportJson.Contains(
                term,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                "The aria2 TLS report contains evidence that is not safe to persist.");
        }

        return reportJson;
    }

    public static async Task WriteAsync(
        string reportPath,
        string reportJson,
        CancellationToken cancellationToken)
    {
        await WriteAsync(
            reportPath,
            reportJson,
            directory => _ = Directory.CreateDirectory(directory),
            File.WriteAllTextAsync,
            cancellationToken).ConfigureAwait(false);
    }

    internal static async Task WriteAsync(
        string reportPath,
        string reportJson,
        Action<string> createDirectory,
        Func<string, string, CancellationToken, Task> writeAllTextAsync,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportJson);
        ArgumentNullException.ThrowIfNull(createDirectory);
        ArgumentNullException.ThrowIfNull(writeAllTextAsync);

        var fullPath = Path.GetFullPath(reportPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (directory != null)
        {
            createDirectory(directory);
        }

        await writeAllTextAsync(
            fullPath,
            reportJson,
            cancellationToken).ConfigureAwait(false);
    }
}
