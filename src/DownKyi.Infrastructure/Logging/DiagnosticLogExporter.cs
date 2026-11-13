using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using DownKyi.Application.Diagnostics;

namespace DownKyi.Infrastructure.Logging;

internal sealed class DiagnosticLogExporter(
    ApplicationLogOptions options,
    ISensitiveDataRedactor redactor,
    ApplicationLogRetentionManager retention,
    TimeProvider timeProvider)
{
    private static readonly byte[] NewLine = Encoding.UTF8.GetBytes(Environment.NewLine);

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
            var (events, truncated) = await ReadNewestEventsAsync(
                SerializeDiagnosticEvent, cancellationToken).ConfigureAwait(false);
            var eventsPath = Path.Combine(diagnosticDirectory, "events.jsonl");
            await WriteEventsAsync(eventsPath, events, cancellationToken).ConfigureAwait(false);

            var manifest = new ApplicationDiagnosticManifest(
                SchemaVersion: 1,
                GeneratedAtUtc: timestamp,
                ApplicationVersion: typeof(DiagnosticLogExporter).Assembly.GetName().Version?.ToString() ?? "unknown",
                Runtime: Environment.Version.ToString(),
                OperatingSystem: redactor.Redact(RuntimeInformation.OSDescription),
                Architecture: RuntimeInformation.ProcessArchitecture.ToString(),
                EventCount: events.Count,
                Files: ["events.jsonl"],
                Redaction:
                [
                    "cookies and request secrets",
                    "email addresses and user identifiers",
                    "file paths"
                ],
                Storage: getMetrics(),
                Truncated: truncated);
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
            var (events, truncated) = await ReadNewestEventsAsync(SerializeFeedbackEvent, cancellationToken)
                .ConfigureAwait(false);
            var manifest = new FeedbackDiagnosticManifest(
                SchemaVersion: 3,
                GeneratedAtUtc: timestamp,
                ApplicationVersion: typeof(DiagnosticLogExporter).Assembly.GetName().Version?.ToString()
                                    ?? "unknown",
                Runtime: Environment.Version.ToString(),
                OperatingSystem: redactor.Redact(RuntimeInformation.OSDescription),
                Architecture: RuntimeInformation.ProcessArchitecture.ToString(),
                EventCount: events.Count,
                Files: ["events.jsonl"],
                Truncated: truncated);
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

    private byte[] SerializeDiagnosticEvent(ApplicationLogRecord record)
    {
        return JsonSerializer.SerializeToUtf8Bytes(
            RedactForExport(record), ApplicationLogJsonContext.Default.ApplicationLogRecord);
    }

    private byte[] SerializeFeedbackEvent(ApplicationLogRecord record)
    {
        return JsonSerializer.SerializeToUtf8Bytes(new FeedbackDiagnosticEvent(
            record.Timestamp,
            record.Level.ToString(),
            record.Category?.StartsWith("DownKyi.", StringComparison.Ordinal) == true
                ? redactor.Redact(record.Category)
                : "external",
            record.EventId.Id,
            redactor.Redact(record.Message),
            redactor.Redact(record.SafeExceptionDiagnostic),
            redactor.Redact(record.ExceptionText)), ApplicationLogJsonContext.Default.FeedbackDiagnosticEvent);
    }

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
        IReadOnlyList<byte[]> events,
        CancellationToken cancellationToken)
    {
        var stream = await archive.CreateEntry("events.jsonl", CompressionLevel.Optimal)
            .OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var buffered = new BufferedStream(stream, 64 * 1024);
            await using (buffered.ConfigureAwait(false))
            {
                await WriteEventsAsync(buffered, events, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<(IReadOnlyList<byte[]> Events, bool Truncated)> ReadNewestEventsAsync(
        Func<ApplicationLogRecord, byte[]> serialize,
        CancellationToken cancellationToken)
    {
        var events = new List<byte[]>();
        long bytes = 0;
        var truncated = false;
        var files = GetEventFiles()
            .Select(static path => new FileInfo(path))
            .OrderByDescending(static file => file.LastWriteTimeUtc)
            .ThenByDescending(static file => file.FullName, StringComparer.Ordinal)
            .ToArray();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var available = options.MaxExportBytes - bytes;
            if (available == 0)
            {
                truncated = true;
                break;
            }

            var fromFile = new Queue<byte[]>();
            long fileBytes = 0;
            var droppedForBudget = false;
            var stream = new FileStream(
                file.FullName,
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

                    var exported = serialize(record);
                    var size = exported.Length + NewLine.Length;
                    if (size > options.MaxExportBytes)
                    {
                        truncated = true;
                        continue;
                    }

                    fromFile.Enqueue(exported);
                    fileBytes += size;
                    while (fileBytes > available)
                    {
                        fileBytes -= fromFile.Dequeue().Length + NewLine.Length;
                        droppedForBudget = true;
                    }
                }
            }

            events.InsertRange(0, fromFile.ToArray());
            bytes += fileBytes;
            if (droppedForBudget)
            {
                // Earlier files cannot contribute to the newest suffix selected within this file.
                truncated = true;
                break;
            }
        }

        return (events, truncated);
    }

    private static async Task WriteEventsAsync(
        string path,
        IReadOnlyList<byte[]> events,
        CancellationToken cancellationToken)
    {
        var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous);
        await using (stream.ConfigureAwait(false))
        {
            await WriteEventsAsync(stream, events, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteEventsAsync(
        Stream stream,
        IReadOnlyList<byte[]> events,
        CancellationToken cancellationToken)
    {
        foreach (var entry in events)
        {
            await stream.WriteAsync(entry, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(NewLine, cancellationToken).ConfigureAwait(false);
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
            ExceptionText = redactor.Redact(record.ExceptionText),
            SafeExceptionDiagnostic = redactor.Redact(record.SafeExceptionDiagnostic)
        };
    }
}
