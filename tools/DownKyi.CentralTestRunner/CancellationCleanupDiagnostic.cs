using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace DownKyi.CentralTestRunner;

internal sealed class CancellationCleanupDiagnostic
{
    internal const string DataKey = "DownKyi.CancellationCleanupDiagnostic";
    private const int EventCapacity = 64;
    private const int MessageCapacity = 512;
    private const int DiagnosticCapacity = 4096;
    private readonly Lock sync = new();
    private readonly List<string> events = [];
    private readonly SensitiveEvidenceRedactor redactor;
    private readonly Stopwatch stopwatch = Stopwatch.StartNew();
    private readonly int rootPid;
    private readonly bool cancellationRequested;

    internal CancellationCleanupDiagnostic(
        int rootPid,
        bool cancellationRequested,
        string diagnosticWorkingDirectory)
    {
        this.rootPid = rootPid;
        this.cancellationRequested = cancellationRequested;
        redactor = new SensitiveEvidenceRedactor(diagnosticWorkingDirectory);
    }

    internal void Record(string stage, int? processId = null, string? detail = null)
    {
        var entry = new StringBuilder()
            .Append("T+")
            .Append(stopwatch.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture))
            .Append("ms stage=")
            .Append(stage);
        if (processId is not null)
        {
            entry.Append(" pid=").Append(processId.Value);
        }

        if (!string.IsNullOrWhiteSpace(detail))
        {
            entry.Append(" detail=").Append(detail);
        }

        lock (sync)
        {
            if (events.Count < EventCapacity)
            {
                events.Add(entry.ToString());
            }
        }
    }

    internal void AttachFailure(
        Exception target,
        string failureStage,
        Exception firstFailure,
        Exception escapingFailure)
    {
        var message = BoundAndFlatten(redactor.Redact(firstFailure.Message), MessageCapacity);
        string[] snapshot;
        lock (sync)
        {
            snapshot = [.. events];
        }

        var diagnostic = new StringBuilder()
            .Append("cancellation-cleanup-failure")
            .Append(" failureStage=").Append(failureStage)
            .Append(" firstException=").Append(firstFailure.GetType().FullName)
            .Append(" firstMessage=").Append(message)
            .Append(" escapingException=").Append(escapingFailure.GetType().FullName)
            .Append(" cancellationRequested=").Append(cancellationRequested)
            .Append(" rootPid=").Append(rootPid)
            .Append(" events=[")
            .Append(string.Join(" | ", snapshot))
            .Append(']')
            .ToString();
        diagnostic = BoundAndFlatten(diagnostic, DiagnosticCapacity);
        target.Data[DataKey] = diagnostic;
    }

    private static string BoundAndFlatten(string value, int capacity)
    {
        var flattened = value.Replace('\r', ' ').Replace('\n', ' ');
        return flattened.Length <= capacity ? flattened : flattened[..capacity];
    }
}
