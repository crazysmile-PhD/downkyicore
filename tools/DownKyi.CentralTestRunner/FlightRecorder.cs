using System.Text.Json;
using System.Text.Json.Serialization;

namespace DownKyi.CentralTestRunner;

internal sealed class FlightRecorder
{
    internal const string SnapshotNotice =
        "Best-effort failure-time snapshot only. Short-lived or orphaned descendants may be absent; absence is not proof that no descendant existed.";

    internal const string DiagnosticGuidance =
        "先根據本報告中的 slice identity、root process identity、child snapshot、timeout、cleanup、stdout/stderr 等 evidence 定位問題。\n\n" +
        "如果約 5 分鐘內仍無法確認問題所在，不要優先增加更多 lifecycle verifier、polling、containment 或監控。\n\n" +
        "這通常代表目前的 process topology、ownership boundary 或 module responsibility 已經複雜到不利於維護。\n\n" +
        "將該區域標記為 maintainability refactor candidate，優先考慮拆成更小、責任更單一、可以獨立診斷的單元。";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly RecorderReport report;
    private readonly TimeSpan snapshotTimeout;
    private readonly Func<int, TimeSpan, Task<FinalProcessSnapshot>> snapshotCapture;

    private FlightRecorder(
        string evidencePath,
        RecorderReport report,
        TimeSpan snapshotTimeout,
        Func<int, TimeSpan, Task<FinalProcessSnapshot>> snapshotCapture,
        SensitiveEvidenceRedactor redactor)
    {
        EvidencePath = evidencePath;
        this.report = report;
        this.snapshotTimeout = snapshotTimeout;
        this.snapshotCapture = snapshotCapture;
        Redactor = redactor;
    }

    public string EvidencePath { get; }

    internal SensitiveEvidenceRedactor Redactor { get; }

    public static async Task<FlightRecorder> CreateAsync(ProcessExecutionRequest request)
    {
        Directory.CreateDirectory(request.EvidenceDirectory);
        var safeSlice = string.Concat(request.SliceIdentity.Select(character =>
            char.IsLetterOrDigit(character) ? character : '-')).Trim('-');
        if (safeSlice.Length > 80)
        {
            safeSlice = safeSlice[..80];
        }
        var evidencePath = Path.Combine(
            request.EvidenceDirectory,
            $"{safeSlice}-{Guid.NewGuid():N}.json");
        var redactor = new SensitiveEvidenceRedactor(request.StartInfo.WorkingDirectory);
        var report = new RecorderReport
        {
            SliceIdentity = redactor.Redact(request.SliceIdentity),
            TestIdentity = redactor.Redact(request.TestIdentity),
            RecorderStartedAtUtc = DateTimeOffset.UtcNow,
            Events = []
        };
        var recorder = new FlightRecorder(
            evidencePath,
            report,
            request.CleanupTimeout,
            request.SnapshotCapture ?? ProcessTreeSnapshot.CaptureAsync,
            redactor);
        await recorder.RecordAsync("recorder_start").ConfigureAwait(false);
        return recorder;
    }

    public void SetRootIdentity(int pid, DateTimeOffset startTimeUtc)
    {
        report.RootProcess = new RootProcessIdentity
        {
            Pid = pid,
            StartTimeUtc = startTimeUtc
        };
    }

    public void SetOutputTails(string standardOutput, string standardError)
    {
        report.StdoutTail = Redactor.Redact(standardOutput);
        report.StderrTail = Redactor.Redact(standardError);
    }

    public async Task RecordAsync(
        string eventName,
        int? pid = null,
        DateTimeOffset? startTimeUtc = null,
        int? exitCode = null,
        string? detail = null)
    {
        report.Events.Add(new RecorderEvent
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Event = eventName,
            Pid = pid,
            StartTimeUtc = startTimeUtc,
            ExitCode = exitCode,
            Detail = detail is null ? null : Redactor.Redact(detail)
        });
        await PersistAsync().ConfigureAwait(false);
    }

    public async Task CaptureFinalSnapshotOnceAsync()
    {
        if (report.FinalSnapshot is not null)
        {
            return;
        }

        var rootPid = report.RootProcess?.Pid ?? 0;
        try
        {
            report.FinalSnapshot = await snapshotCapture(rootPid, snapshotTimeout).ConfigureAwait(false);
            await RecordAsync("final_snapshot", pid: rootPid).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or
                                          System.ComponentModel.Win32Exception or TimeoutException or
                                          UnauthorizedAccessException or NotSupportedException)
        {
            var error = Redactor.Redact(exception.Message);
            report.FinalSnapshot = new FinalProcessSnapshot
            {
                CapturedAtUtc = DateTimeOffset.UtcNow,
                Completeness = SnapshotNotice,
                Processes = [],
                Error = error
            };
            await RecordAsync(
                "final_snapshot_failed",
                pid: rootPid,
                detail: error).ConfigureAwait(false);
        }
    }

    public async Task FinalizeFailureAsync(
        string outcome,
        TailBuffer standardOutput,
        TailBuffer standardError)
    {
        await CaptureFinalSnapshotOnceAsync().ConfigureAwait(false);

        report.Outcome = outcome;
        if (standardOutput.Value.Length > 0 || report.StdoutTail is null)
        {
            report.StdoutTail = Redactor.Redact(standardOutput.Value);
        }
        if (standardError.Value.Length > 0 || report.StderrTail is null)
        {
            report.StderrTail = Redactor.Redact(standardError.Value);
        }

        report.DiagnosticGuidance = DiagnosticGuidance;
        await PersistAsync().ConfigureAwait(false);
    }

    private Task PersistAsync()
    {
        var json = JsonSerializer.Serialize(report, JsonOptions);
        return File.WriteAllTextAsync(EvidencePath, json);
    }
}
