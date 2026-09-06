using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace DownKyi.CodeMetricsAudit;

internal static class Ca1506ReportWriter
{
    internal const string JsonReportFileName = "ca1506-report.json";
    internal const string MarkdownReportFileName = "ca1506-report.md";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static void Write(string outputDirectory, Ca1506Report report)
    {
        Write(outputDirectory, report, static (source, destination) => File.Move(source, destination, overwrite: true));
    }

    internal static void Write(
        string outputDirectory,
        Ca1506Report report,
        Action<string, string> publishFile)
    {
        Write(
            outputDirectory,
            report,
            publishFile,
            CreateBackupBundle,
            ValidateBackupBundle,
            File.Delete);
    }

    internal static void Write(
        string outputDirectory,
        Ca1506Report report,
        Action<string, string> publishFile,
        Action<string, string, string> createBackupBundle,
        Action<string, string, string> validateBackupBundle,
        Action<string> deleteBackupBundle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(publishFile);
        ArgumentNullException.ThrowIfNull(createBackupBundle);
        ArgumentNullException.ThrowIfNull(validateBackupBundle);
        ArgumentNullException.ThrowIfNull(deleteBackupBundle);
        Directory.CreateDirectory(outputDirectory);

        var jsonPath = Path.Combine(outputDirectory, JsonReportFileName);
        var markdownPath = Path.Combine(outputDirectory, MarkdownReportFileName);
        if (Directory.Exists(jsonPath) || Directory.Exists(markdownPath))
        {
            throw new IOException("CA1506 audit report destination is not a file.");
        }

        var operationId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var stagedJsonPath = Path.Combine(outputDirectory, $".ca1506-report-{operationId}.json.tmp");
        var stagedMarkdownPath = Path.Combine(outputDirectory, $".ca1506-report-{operationId}.md.tmp");
        var stagedBackupBundlePath = Path.Combine(
            outputDirectory,
            $".ca1506-report-{operationId}.backup.zip.tmp");
        var backupBundlePath = Path.Combine(outputDirectory, $".ca1506-report-{operationId}.backup.zip");
        try
        {
            File.WriteAllText(
                stagedJsonPath,
                JsonSerializer.Serialize(report, JsonOptions) + "\n",
                new UTF8Encoding(false));
            File.WriteAllText(stagedMarkdownPath, BuildMarkdown(report), new UTF8Encoding(false));

            var jsonExists = File.Exists(jsonPath);
            var markdownExists = File.Exists(markdownPath);
            if (jsonExists != markdownExists)
            {
                throw new IOException("Existing CA1506 audit reports are not a complete pair.");
            }

            var hasPreviousReports = jsonExists;
            if (hasPreviousReports)
            {
                createBackupBundle(jsonPath, markdownPath, stagedBackupBundlePath);
                validateBackupBundle(stagedBackupBundlePath, jsonPath, markdownPath);
                File.Move(stagedBackupBundlePath, backupBundlePath);
            }

            try
            {
                publishFile(stagedJsonPath, jsonPath);
                publishFile(stagedMarkdownPath, markdownPath);
            }
            catch (Exception publicationFailure)
            {
                try
                {
                    RollBackPublication(
                        jsonPath,
                        markdownPath,
                        backupBundlePath,
                        operationId,
                        hasPreviousReports);
                }
                catch (Exception rollbackFailure)
                {
                    throw new IOException(
                        "CA1506 audit report publication and rollback failed.",
                        new AggregateException(publicationFailure, rollbackFailure));
                }

                if (hasPreviousReports)
                {
                    try
                    {
                        DeleteBackupBundle(backupBundlePath, deleteBackupBundle);
                    }
                    catch (Exception cleanupFailure)
                    {
                        throw new IOException(
                            "CA1506 audit report publication failed and backup cleanup failed.",
                            new AggregateException(publicationFailure, cleanupFailure));
                    }
                }

                throw;
            }

            if (hasPreviousReports)
            {
                DeleteBackupBundle(backupBundlePath, deleteBackupBundle);
            }
        }
        finally
        {
            File.Delete(stagedJsonPath);
            File.Delete(stagedMarkdownPath);
            File.Delete(stagedBackupBundlePath);
        }

        if (!File.Exists(jsonPath) || !File.Exists(markdownPath))
        {
            throw new IOException("CA1506 audit reports were not produced.");
        }
    }

    internal static void CreateBackupBundle(string jsonPath, string markdownPath, string bundlePath)
    {
        using var archive = ZipFile.Open(bundlePath, ZipArchiveMode.Create);
        archive.CreateEntryFromFile(jsonPath, JsonReportFileName, CompressionLevel.NoCompression);
        archive.CreateEntryFromFile(markdownPath, MarkdownReportFileName, CompressionLevel.NoCompression);
    }

    internal static void ValidateBackupBundle(string bundlePath, string jsonPath, string markdownPath)
    {
        using var archive = ZipFile.OpenRead(bundlePath);
        if (archive.Entries.Count != 2)
        {
            throw new InvalidDataException("CA1506 audit backup bundle does not contain exactly two reports.");
        }

        ValidateBackupEntry(archive, JsonReportFileName, jsonPath);
        ValidateBackupEntry(archive, MarkdownReportFileName, markdownPath);
    }

    private static void RollBackPublication(
        string jsonPath,
        string markdownPath,
        string backupBundlePath,
        string operationId,
        bool hasPreviousReports)
    {
        if (!hasPreviousReports)
        {
            File.Delete(jsonPath);
            File.Delete(markdownPath);
            return;
        }

        RestorePublishedReports(
            backupBundlePath,
            jsonPath,
            markdownPath,
            operationId);
    }

    private static void RestorePublishedReports(
        string bundlePath,
        string jsonPath,
        string markdownPath,
        string operationId)
    {
        var outputDirectory = Path.GetDirectoryName(jsonPath)
            ?? throw new InvalidOperationException("CA1506 audit report has no output directory.");
        var restoreJsonPath = Path.Combine(outputDirectory, $".ca1506-report-{operationId}.json.restore.tmp");
        var restoreMarkdownPath = Path.Combine(outputDirectory, $".ca1506-report-{operationId}.md.restore.tmp");
        try
        {
            using (var archive = ZipFile.OpenRead(bundlePath))
            {
                ExtractBackupEntry(archive, JsonReportFileName, restoreJsonPath);
                ExtractBackupEntry(archive, MarkdownReportFileName, restoreMarkdownPath);
            }

            File.Move(restoreJsonPath, jsonPath, overwrite: true);
            File.Move(restoreMarkdownPath, markdownPath, overwrite: true);
        }
        finally
        {
            File.Delete(restoreJsonPath);
            File.Delete(restoreMarkdownPath);
        }
    }

    private static void ValidateBackupEntry(ZipArchive archive, string entryName, string reportPath)
    {
        var entry = archive.GetEntry(entryName)
            ?? throw new InvalidDataException($"CA1506 audit backup bundle is missing {entryName}.");
        using var entryStream = entry.Open();
        using var content = new MemoryStream();
        entryStream.CopyTo(content);
        if (!File.ReadAllBytes(reportPath).AsSpan().SequenceEqual(content.ToArray()))
        {
            throw new InvalidDataException($"CA1506 audit backup bundle has incomplete {entryName} content.");
        }
    }

    private static void ExtractBackupEntry(ZipArchive archive, string entryName, string destinationPath)
    {
        var entry = archive.GetEntry(entryName)
            ?? throw new InvalidDataException($"CA1506 audit backup bundle is missing {entryName}.");
        using var entryStream = entry.Open();
        using var destination = File.Create(destinationPath);
        entryStream.CopyTo(destination);
    }

    private static void DeleteBackupBundle(string bundlePath, Action<string> deleteBackupBundle)
    {
        try
        {
            deleteBackupBundle(bundlePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException("CA1506 audit backup bundle cleanup failed.", exception);
        }

        if (File.Exists(bundlePath))
        {
            throw new IOException("CA1506 audit backup bundle cleanup did not remove the bundle.");
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
