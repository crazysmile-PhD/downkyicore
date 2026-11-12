using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DownKyi.Application.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DownKyi.Infrastructure.Logging;

internal sealed partial class DiagnosticLogExporter(
    ApplicationLogOptions options,
    ISensitiveDataRedactor redactor,
    ApplicationLogRetentionManager retention,
    TimeProvider timeProvider)
{
    private const string TransferCoordinatorCategory =
        "DownKyi.Services.Download.DownloadTransferCoordinator";
    private const string DownloadPipelineCategory =
        "DownKyi.Services.Download.DownloadPipeline";
    private const string FfmpegProcessorCategory =
        "DownKyi.Infrastructure.FFmpeg.FfmpegProcessor";
    private const string FfmpegConcatRuntimeCategory =
        "DownKyi.Infrastructure.FFmpeg.FfmpegConcatRuntime";

    public async Task<string> ExportAsync(
        Func<ApplicationLogMetrics> getMetrics,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(getMetrics);
        var timestamp = timeProvider.GetUtcNow().ToUniversalTime();
        var diagnosticDirectory = retention.ReserveDiagnosticDirectory(timestamp);
        var completed = false;
        try
        {
            var records = await ReadNewestRecordsAsync(cancellationToken).ConfigureAwait(false);
            var eventsPath = Path.Combine(diagnosticDirectory, "events.jsonl");
            await WriteEventsAsync(eventsPath, records, cancellationToken).ConfigureAwait(false);

            var manifest = new ApplicationDiagnosticManifest(
                SchemaVersion: 1,
                GeneratedAtUtc: timestamp,
                ApplicationVersion: typeof(DiagnosticLogExporter).Assembly.GetName().Version?.ToString() ?? "unknown",
                Runtime: Environment.Version.ToString(),
                OperatingSystem: redactor.Redact(RuntimeInformation.OSDescription),
                Architecture: RuntimeInformation.ProcessArchitecture.ToString(),
                EventCount: records.Count,
                Files: ["events.jsonl"],
                Redaction:
                [
                    "cookies and request secrets",
                    "email addresses and user identifiers",
                    "personal directory prefixes"
                ],
                Storage: getMetrics());
            var manifestPath = Path.Combine(diagnosticDirectory, "manifest.json");
            await File.WriteAllTextAsync(
                    manifestPath,
                    JsonSerializer.Serialize(
                        manifest,
                        ApplicationLogJsonContext.Default.ApplicationDiagnosticManifest),
                    new UTF8Encoding(false),
                    cancellationToken)
                .ConfigureAwait(false);
            completed = true;
            return manifestPath;
        }
        finally
        {
            retention.ReleaseDiagnosticDirectory(diagnosticDirectory, delete: !completed);
        }
    }

    public async Task<string> ExportFeedbackPackageAsync(CancellationToken cancellationToken)
    {
        var timestamp = timeProvider.GetUtcNow().ToUniversalTime();
        var diagnosticDirectory = retention.ReserveDiagnosticDirectory(timestamp);
        var completed = false;
        try
        {
            var records = await ReadNewestRecordsAsync(cancellationToken, errorsOnly: true)
                .ConfigureAwait(false);
            var events = records.Select(record => new FeedbackDiagnosticEvent(
                record.Timestamp,
                record.Level.ToString(),
                record.Category.StartsWith("DownKyi.", StringComparison.Ordinal)
                    ? record.Category
                    : "external",
                record.EventId.Id,
                redactor.Redact(ProjectFeedbackMessage(record)),
                record.SafeExceptionDiagnostic)).ToArray();
            var manifest = new FeedbackDiagnosticManifest(
                SchemaVersion: 2,
                GeneratedAtUtc: timestamp,
                ApplicationVersion: typeof(DiagnosticLogExporter).Assembly.GetName().Version?.ToString()
                                    ?? "unknown",
                Runtime: Environment.Version.ToString(),
                OperatingSystem: RuntimeInformation.OSDescription,
                Architecture: RuntimeInformation.ProcessArchitecture.ToString(),
                EventCount: events.Length,
                Files: ["errors.jsonl"]);
            var archivePath = Path.Combine(diagnosticDirectory, "downkyi-feedback.zip");
            var stream = new FileStream(
                archivePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
            {
                await WriteFeedbackManifestAsync(archive, manifest, cancellationToken)
                    .ConfigureAwait(false);
                await WriteFeedbackEventsAsync(archive, events, cancellationToken)
                    .ConfigureAwait(false);
            }

            completed = true;
            return archivePath;
        }
        finally
        {
            retention.ReleaseDiagnosticDirectory(diagnosticDirectory, delete: !completed);
        }
    }

    private static string ProjectFeedbackMessage(ApplicationLogRecord record)
    {
        var isAllowed = record.Category switch
        {
            TransferCoordinatorCategory => TransferAttemptDiagnosticRegex().IsMatch(record.Message),
            DownloadPipelineCategory => DownloadPipelineFailureRegex().IsMatch(record.Message),
            FfmpegProcessorCategory or FfmpegConcatRuntimeCategory =>
                FfmpegFailureDiagnosticRegex().IsMatch(record.Message),
            _ => false
        };

        return isAllowed ? record.Message : string.Empty;
    }

    [GeneratedRegex(
        "\\ADownload transfer attempt failed; backend=built-in; " +
        "attempt=[1-9][0-9]*/[1-9][0-9]*; " +
        "failure=(?:TransientNetwork|RateLimited|ExpiredAddress|ResumeRejected|InvalidMedia|CandidateRejected|Disk|Tls|Permanent); " +
        "error=download\\.transfer\\.[a-z0-9.-]+; " +
        "next=(?:Stop|RetrySameAddress|TryNextAddress|RefreshAddresses); " +
        "delayMilliseconds=(?:0|[1-9][0-9]*)\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex TransferAttemptDiagnosticRegex();

    [GeneratedRegex(
        "\\ADownload stage (?:[A-Z][A-Za-z0-9]*|unknown) failed with " +
        "(?:download\\.[a-z0-9.-]+|unknown)\\.\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex DownloadPipelineFailureRegex();

    [GeneratedRegex(
        "\\AFFmpeg operation failed\\. operation=[A-Za-z][A-Za-z0-9-]*; " +
        "exit=-?(?:0|[1-9][0-9]*); timedOut=(?:True|False)\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex FfmpegFailureDiagnosticRegex();

    private static async Task WriteFeedbackManifestAsync(
        ZipArchive archive,
        FeedbackDiagnosticManifest manifest,
        CancellationToken cancellationToken)
    {
        var stream = await archive.CreateEntry("manifest.json", CompressionLevel.Optimal)
            .OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await using (writer.ConfigureAwait(false))
            {
                await writer.WriteAsync(
                    JsonSerializer.Serialize(manifest, ApplicationLogJsonContext.Default.FeedbackDiagnosticManifest)
                        .AsMemory(),
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task WriteFeedbackEventsAsync(
        ZipArchive archive,
        IReadOnlyList<FeedbackDiagnosticEvent> events,
        CancellationToken cancellationToken)
    {
        var stream = await archive.CreateEntry("errors.jsonl", CompressionLevel.Optimal)
            .OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await using (writer.ConfigureAwait(false))
            {
                foreach (var record in events)
                {
                    await writer.WriteLineAsync(
                        JsonSerializer.Serialize(record, ApplicationLogJsonContext.Default.FeedbackDiagnosticEvent)
                            .AsMemory(),
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task<IReadOnlyList<ApplicationLogRecord>> ReadNewestRecordsAsync(
        CancellationToken cancellationToken,
        bool errorsOnly = false)
    {
        var records = new List<ApplicationLogRecord>(options.RecentEventCapacity);
        var files = GetEventFiles()
            .Select(static path => new FileInfo(path))
            .OrderByDescending(static file => file.LastWriteTimeUtc)
            .ThenByDescending(static file => file.FullName, StringComparer.Ordinal)
            .ToArray();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = options.RecentEventCapacity - records.Count;
            if (remaining == 0)
            {
                break;
            }

            var fromFile = await ReadLastRecordsFromFileAsync(
                    file.FullName,
                    remaining,
                    errorsOnly,
                    cancellationToken)
                .ConfigureAwait(false);
            records.InsertRange(0, fromFile);
        }

        return records;
    }

    private async Task<List<ApplicationLogRecord>> ReadLastRecordsFromFileAsync(
        string path,
        int capacity,
        bool errorsOnly,
        CancellationToken cancellationToken)
    {
        var queue = new Queue<ApplicationLogRecord>(capacity);
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (stream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                ApplicationLogRecord? record;
                try
                {
                    record = ApplicationLogSerializer.Deserialize(line);
                }
                catch (JsonException)
                {
                    retention.RecordMalformedExport();
                    continue;
                }

                if (record == null)
                {
                    retention.RecordMalformedExport();
                    continue;
                }

                if (errorsOnly && (record.Level < LogLevel.Warning || record.Level >= LogLevel.None))
                {
                    continue;
                }

                queue.Enqueue(errorsOnly ? record : RedactForExport(record));
                while (queue.Count > capacity)
                {
                    queue.Dequeue();
                }
            }
        }

        return queue.ToList();
    }

    private static async Task WriteEventsAsync(
        string path,
        IReadOnlyList<ApplicationLogRecord> records,
        CancellationToken cancellationToken)
    {
        var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (stream.ConfigureAwait(false))
        {
            var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await using (writer.ConfigureAwait(false))
            {
                foreach (var record in records)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await writer.WriteLineAsync(
                            ApplicationLogSerializer.Serialize(record).AsMemory(),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
    }

    private IEnumerable<string> GetEventFiles()
    {
        if (!Directory.Exists(options.LogDirectory))
        {
            return [];
        }

        return Directory.GetDirectories(
                options.LogDirectory,
                "????-??-??",
                SearchOption.TopDirectoryOnly)
            .SelectMany(directory => Directory.GetFiles(
                directory,
                ApplicationLogRetentionManager.EventFilePattern,
                SearchOption.TopDirectoryOnly));
    }

    private ApplicationLogRecord RedactForExport(ApplicationLogRecord record)
    {
        return record with
        {
            Category = redactor.Redact(record.Category),
            EventId = new Microsoft.Extensions.Logging.EventId(
                record.EventId.Id,
                redactor.Redact(record.EventId.Name)),
            Message = redactor.Redact(record.Message),
            ExceptionType = redactor.Redact(record.ExceptionType),
            Scope = redactor.Redact(record.Scope),
            ExceptionText = redactor.Redact(record.ExceptionText)
        };
    }
}
