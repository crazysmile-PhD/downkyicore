using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DownKyi.CodeMetricsAudit;

internal static class Ca1506ReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static void Write(string outputDirectory, Ca1506Report report)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(report);
        Directory.CreateDirectory(outputDirectory);

        var jsonPath = Path.Combine(outputDirectory, "ca1506-report.json");
        var markdownPath = Path.Combine(outputDirectory, "ca1506-report.md");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, JsonOptions) + "\n", new UTF8Encoding(false));
        File.WriteAllText(markdownPath, BuildMarkdown(report), new UTF8Encoding(false));

        if (!File.Exists(jsonPath) || !File.Exists(markdownPath))
        {
            throw new IOException("CA1506 audit reports were not produced.");
        }
    }

    private static string BuildMarkdown(Ca1506Report report)
    {
        var markdown = new StringBuilder();
        markdown.AppendLine("# CA1506 Architecture Audit");
        markdown.AppendLine();
        markdown.AppendLine(CultureInfo.InvariantCulture, $"- Commit: `{report.Commit}`");
        markdown.AppendLine(
            CultureInfo.InvariantCulture,
            $"- Dirty tracked worktree: **{(report.DirtyWorktree ? "true" : "false")}**");
        markdown.AppendLine(CultureInfo.InvariantCulture, $"- Unique findings: **{report.Summary.Total}**");
        markdown.AppendLine(CultureInfo.InvariantCulture, $"- Production findings: **{report.Summary.Production}**");
        markdown.AppendLine(CultureInfo.InvariantCulture, $"- Test findings: **{report.Summary.Test}**");
        markdown.AppendLine();
        markdown.AppendLine(
            "CA1506 findings are advisory and do not fail this audit. A failed build, malformed input, " +
            "missing SARIF, or report-write failure does fail it.");
        markdown.AppendLine();
        markdown.AppendLine("## Classification summary");
        markdown.AppendLine();
        markdown.AppendLine("| Classification | Count |");
        markdown.AppendLine("| --- | ---: |");
        foreach (var classification in Ca1506ReportGenerator.ClassificationOrder)
        {
            markdown.AppendLine(
                CultureInfo.InvariantCulture,
                $"| {classification} | {report.Summary.Classifications[classification]} |");
        }

        markdown.AppendLine();
        markdown.AppendLine("## Findings");
        markdown.AppendLine();
        if (report.Findings.Count == 0)
        {
            markdown.AppendLine("No CA1506 findings were reported.");
            return markdown.ToString();
        }

        markdown.AppendLine("| Scope | Classification | Project | Location | Diagnostic | Review |");
        markdown.AppendLine("| --- | --- | --- | --- | --- | --- |");
        foreach (var finding in report.Findings)
        {
            var location = $"{finding.File}:{finding.Line}:{finding.Column}";
            markdown.AppendLine(
                CultureInfo.InvariantCulture,
                $"| {finding.Scope} | {finding.Classification} | {finding.Project} | `{location}` | " +
                $"{EscapeCell(finding.Message)} | {EscapeCell(finding.Rationale)} |");
        }

        return markdown.ToString();
    }

    private static string EscapeCell(string value)
    {
        return value.Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
    }
}
