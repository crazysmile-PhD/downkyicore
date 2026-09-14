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
    private readonly Func<int, TimeSpan, FinalProcessSnapshot> snapshotCapture;

    private FlightRecorder(
        string evidencePath,
        RecorderReport report,
        TimeSpan snapshotTimeout,
        Func<int, TimeSpan, FinalProcessSnapshot> snapshotCapture,
        SensitiveEvidenceRedactor redactor)
    {
        EvidencePath = evidencePath;
        this.report = report;
        this.snapshotTimeout = snapshotTimeout;
        this.snapshotCapture = snapshotCapture;
        Redactor = redactor;
    }

    public string EvidencePath { get; }
    internal string StdoutTail => report.StdoutTail ?? string.Empty;
    internal string StderrTail => report.StderrTail ?? string.Empty;

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
            request.SnapshotCapture ?? ProcessTreeSnapshot.Capture,
            redactor);
        await recorder.RecordAsync("recorder_start").ConfigureAwait(false);
        await recorder.RecordAsync("scope_launch_begin").ConfigureAwait(false);
        return recorder;
    }

    internal static Task PreserveCommandFailureAsync(
        string evidenceDirectory,
        string repositoryRoot,
        string commandIdentity,
        Exception primaryFailure)
    {
        Directory.CreateDirectory(evidenceDirectory);
        var redactor = new SensitiveEvidenceRedactor(repositoryRoot);
        var primary = primaryFailure is AggregateException { InnerExceptions.Count: > 0 } aggregate
            ? aggregate.InnerExceptions[0] : primaryFailure;
        var secondary = primaryFailure is AggregateException { InnerExceptions.Count: > 1 } failures
            ? redactor.Redact(failures.InnerExceptions[1].Message) : null;
        var failure = redactor.Redact(primary.Message);
        var report = new RecorderReport
        {
            SliceIdentity = "runner-command",
            TestIdentity = redactor.Redact(commandIdentity),
            RecorderStartedAtUtc = DateTimeOffset.UtcNow,
            Outcome = "command_failed",
            PrimaryFailure = failure,
            SecondaryCleanupFailure = secondary,
            DiagnosticGuidance = DiagnosticGuidance,
            Events =
            [
                new RecorderEvent
                {
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Event = "command_failed",
                    Detail = failure
                }
            ]
        };
        var path = Path.Combine(evidenceDirectory, $"runner-command-{Guid.NewGuid():N}.json");
        return File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, JsonOptions));
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

    internal void SetLifecycleFailure(ProcessLifecycleOwner owner)
    {
        report.PrimaryFailure = owner.PrimaryFailure is null
            ? null : Redactor.Redact(owner.PrimaryFailure.Message);
        report.PrimaryEvidence = owner.LiveEvidence is null
            ? null : Redactor.Redact(owner.LiveEvidence);
        report.SecondaryCleanupFailure = owner.SecondaryCleanupFailure is null
            ? null : Redactor.Redact(owner.SecondaryCleanupFailure.Message);
    }

    internal void SetStartupFailure(Exception primary, Exception secondary)
    {
        report.PrimaryFailure = Redactor.Redact(primary.Message);
        report.SecondaryCleanupFailure = Redactor.Redact(secondary.Message);
    }

    internal void SetPostExitFailure(string failure)
    {
        report.PrimaryFailure ??= Redactor.Redact(failure);
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

    public async Task CaptureFinalSnapshotOnceAsync(
        CleanupDeadline? deadline = null, Action<Exception>? onUnexpectedFailure = null)
    {
        if (report.FinalSnapshot is not null)
        {
            return;
        }

        var rootPid = report.RootProcess?.Pid ?? 0;
        try
        {
            var window = deadline?.SnapshotWindow ?? (snapshotTimeout < TimeSpan.FromSeconds(1)
                ? snapshotTimeout
                : TimeSpan.FromSeconds(1));
            // The diagnostic backend starts no helper process or task.
            report.FinalSnapshot = snapshotCapture(rootPid, window);
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
            await RecordSnapshotFailureAsync(exception, rootPid, deadline).ConfigureAwait(false);
        }
        catch (Exception exception) when (onUnexpectedFailure is not null)
        {
            onUnexpectedFailure!(exception);
            await RecordSnapshotFailureAsync(exception, rootPid, deadline).ConfigureAwait(false);
        }
    }

    private async Task RecordSnapshotFailureAsync(
        Exception exception, int rootPid, CleanupDeadline? deadline)
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

    public async Task FinalizeFailureAsync(
        string outcome,
        TailBuffer standardOutput,
        TailBuffer standardError,
        CleanupDeadline? deadline = null,
        Action<Exception>? onUnexpectedSnapshotFailure = null)
    {
        var unexpectedSnapshotFailure = false;
        await CaptureFinalSnapshotOnceAsync(deadline,
            onUnexpectedSnapshotFailure is null ? null : exception =>
            {
                onUnexpectedSnapshotFailure(exception);
                unexpectedSnapshotFailure = true;
            }).ConfigureAwait(false);

        report.Outcome = unexpectedSnapshotFailure ? "cleanup_failed" : outcome;
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
            if (deadline.Remaining == TimeSpan.Zero)
            {
                throw new TimeoutException("No cleanup budget remains to persist failure evidence.");
            }
            using var persistenceCancellation = new CancellationTokenSource(deadline.Remaining);
            await PersistAsync(persistenceCancellation.Token).ConfigureAwait(false);
        }
    }

    private Task PersistAsync(CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(report, JsonOptions);
        return File.WriteAllTextAsync(EvidencePath, json, cancellationToken);
    }
}
