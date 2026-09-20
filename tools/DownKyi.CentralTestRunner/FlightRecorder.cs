using System.Text.Json;
using System.Text.Json.Serialization;
using DownKyi.ProcessSupervision;

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
    private readonly TimeSpan recorderTimeout;
    private readonly Func<int, TimeSpan, Task<FinalProcessSnapshot>> snapshotCapture;

    private FlightRecorder(
        string evidencePath,
        RecorderReport report,
        TimeSpan recorderTimeout,
        Func<int, TimeSpan, Task<FinalProcessSnapshot>> snapshotCapture,
        SensitiveEvidenceRedactor redactor)
    {
        EvidencePath = evidencePath;
        this.report = report;
        this.recorderTimeout = recorderTimeout;
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
        await recorder.RecordAsync("scope_launch_begin").ConfigureAwait(false);
        return recorder;
    }

    public void SetRootIdentity(int pid, DateTimeOffset? startTimeUtc)
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
        RecordInMemory(eventName, pid, startTimeUtc, exitCode, detail);
        await PersistAsync().ConfigureAwait(false);
    }

    internal void RecordInMemory(
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
    }

    public async Task CaptureFinalSnapshotOnceAsync(CleanupDeadline? deadline = null)
    {
        if (report.FinalSnapshot is not null)
        {
            return;
        }

        var rootPid = report.RootProcess?.Pid ?? 0;
        try
        {
            var window = deadline?.SnapshotWindow ?? (recorderTimeout < TimeSpan.FromSeconds(1)
                ? recorderTimeout
                : TimeSpan.FromSeconds(1));
            report.FinalSnapshot = await Task.Run(() => snapshotCapture(rootPid, window))
                .WaitAsync(window).ConfigureAwait(false);
            if (deadline is null)
            {
                await RecordAsync("final_snapshot", pid: rootPid).ConfigureAwait(false);
            }
            else
            {
                RecordInMemory("final_snapshot", pid: rootPid);
            }
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
            if (deadline is null)
            {
                await RecordAsync("final_snapshot_failed", pid: rootPid, detail: error).ConfigureAwait(false);
            }
            else
            {
                RecordInMemory("final_snapshot_failed", pid: rootPid, detail: error);
            }
        }
    }

    public async Task FinalizeFailureAsync(
        string outcome,
        TailBuffer standardOutput,
        TailBuffer standardError,
        CleanupDeadline? deadline = null)
    {
        await CaptureFinalSnapshotOnceAsync(deadline).ConfigureAwait(false);

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
        if (deadline is null)
        {
            await PersistAsync().ConfigureAwait(false);
        }
        else
        {
            var persistenceDeadline = new CleanupDeadline(recorderTimeout);
            await PersistWithinDeadlineAsync(persistenceDeadline, PersistAsync).ConfigureAwait(false);
        }
    }

    internal static async Task PersistWithinDeadlineAsync(
        CleanupDeadline deadline, Func<CancellationToken, Task> persistAsync)
    {
        using var cancellation = new CancellationTokenSource(deadline.Remaining);
        try
        {
            await persistAsync(cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (cancellation.IsCancellationRequested)
        {
            throw new TimeoutException(
                "Recorder persistence exceeded the cleanup deadline.", exception);
        }
    }

    private Task PersistAsync(CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(report, JsonOptions);
        return File.WriteAllTextAsync(EvidencePath, json, cancellationToken);
    }
}
