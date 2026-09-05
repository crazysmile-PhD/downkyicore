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
        Write(outputDirectory, report, static (source, destination) => File.Move(source, destination));
    }

    internal static void Write(
        string outputDirectory,
        Ca1506Report report,
        Action<string, string> publishFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(publishFile);
        Directory.CreateDirectory(outputDirectory);

        var jsonPath = Path.Combine(outputDirectory, "ca1506-report.json");
        var markdownPath = Path.Combine(outputDirectory, "ca1506-report.md");
        if (Directory.Exists(jsonPath) || Directory.Exists(markdownPath))
        {
            throw new IOException("CA1506 audit report destination is not a file.");
        }

        var operationId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var stagedJsonPath = Path.Combine(outputDirectory, $".ca1506-report-{operationId}.json.tmp");
        var stagedMarkdownPath = Path.Combine(outputDirectory, $".ca1506-report-{operationId}.md.tmp");
        var backupJsonPath = Path.Combine(outputDirectory, $".ca1506-report-{operationId}.json.bak");
        var backupMarkdownPath = Path.Combine(outputDirectory, $".ca1506-report-{operationId}.md.bak");
        var publishedJson = false;
        var publishedMarkdown = false;
        try
        {
            File.WriteAllText(
                stagedJsonPath,
                JsonSerializer.Serialize(report, JsonOptions) + "\n",
                new UTF8Encoding(false));
            File.WriteAllText(stagedMarkdownPath, BuildMarkdown(report), new UTF8Encoding(false));

            BackupExistingReport(jsonPath, backupJsonPath);
            try
            {
                BackupExistingReport(markdownPath, backupMarkdownPath);
            }
            catch
            {
                RestoreReport(backupJsonPath, jsonPath);
                throw;
            }

            try
            {
                publishFile(stagedJsonPath, jsonPath);
                publishedJson = true;
                publishFile(stagedMarkdownPath, markdownPath);
                publishedMarkdown = true;
            }
            catch (Exception exception)
            {
                RollBackPublication(
                    jsonPath,
                    markdownPath,
                    backupJsonPath,
                    backupMarkdownPath,
                    publishedJson,
                    publishedMarkdown,
                    exception);
                throw;
            }

            File.Delete(backupJsonPath);
            File.Delete(backupMarkdownPath);
        }
        finally
        {
            File.Delete(stagedJsonPath);
            File.Delete(stagedMarkdownPath);
        }

        if (!File.Exists(jsonPath) || !File.Exists(markdownPath))
        {
            throw new IOException("CA1506 audit reports were not produced.");
        }
    }

    private static void BackupExistingReport(string reportPath, string backupPath)
    {
        if (File.Exists(reportPath))
        {
            File.Move(reportPath, backupPath);
        }
    }

    private static void RestoreReport(string backupPath, string reportPath)
    {
        if (File.Exists(backupPath))
        {
            File.Move(backupPath, reportPath, overwrite: true);
        }
    }

    private static void RollBackPublication(
        string jsonPath,
        string markdownPath,
        string backupJsonPath,
        string backupMarkdownPath,
        bool publishedJson,
        bool publishedMarkdown,
        Exception publicationFailure)
    {
        try
        {
            if (publishedJson)
            {
                File.Delete(jsonPath);
            }

            if (publishedMarkdown)
            {
                File.Delete(markdownPath);
            }

            RestoreReport(backupJsonPath, jsonPath);
            RestoreReport(backupMarkdownPath, markdownPath);
        }
        catch (Exception rollbackFailure)
        {
            throw new IOException(
                "CA1506 audit report publication and rollback failed.",
                new AggregateException(publicationFailure, rollbackFailure));
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
            $"- Dirty worktree (including untracked files): **{(report.DirtyWorktree ? "true" : "false")}**");
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
