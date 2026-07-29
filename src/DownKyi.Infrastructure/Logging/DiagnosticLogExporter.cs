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

    private async Task<IReadOnlyList<ApplicationLogRecord>> ReadNewestRecordsAsync(
        CancellationToken cancellationToken)
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
                    cancellationToken)
                .ConfigureAwait(false);
            records.InsertRange(0, fromFile);
        }

        return records;
    }

    private async Task<List<ApplicationLogRecord>> ReadLastRecordsFromFileAsync(
        string path,
        int capacity,
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

                queue.Enqueue(RedactForExport(record));
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
