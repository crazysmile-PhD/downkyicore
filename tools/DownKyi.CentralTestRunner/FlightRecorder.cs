using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DownKyi.CentralTestRunner;

internal sealed record ProcessExecutionRequest(
    string SliceIdentity,
    string TestIdentity,
    ProcessStartInfo StartInfo,
    TimeSpan Timeout,
    TimeSpan CleanupTimeout,
    string EvidenceDirectory);

internal sealed record ProcessExecutionResult(
    int ExitCode,
    int RootPid,
    DateTimeOffset RootStartTimeUtc,
    string EvidencePath,
    FlightRecorder Recorder);

internal static class FlightRecorderExecution
{
    public static async Task<ProcessExecutionResult> RunAsync(
        ProcessExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SliceIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TestIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.EvidenceDirectory);

        var recorder = await FlightRecorder.CreateAsync(request).ConfigureAwait(false);
        using var process = new Process { StartInfo = request.StartInfo };
        var standardOutput = new TailBuffer(8192);
        var standardError = new TailBuffer(8192);

        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            await recorder.RecordAsync("process_start_failed", detail: exception.Message).ConfigureAwait(false);
            await recorder.FinalizeFailureAsync("start_failed", standardOutput, standardError).ConfigureAwait(false);
            return new ProcessExecutionResult(2, 0, default, recorder.EvidencePath, recorder);
        }

        var rootPid = process.Id;
        DateTimeOffset rootStartTime;
        try
        {
            rootStartTime = process.StartTime.ToUniversalTime();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            await recorder.RecordAsync(
                "root_identity_failed",
                pid: rootPid,
                detail: exception.Message).ConfigureAwait(false);
            await StopAsync(process, request.CleanupTimeout, recorder).ConfigureAwait(false);
            await recorder.FinalizeFailureAsync("root_identity_failed", standardOutput, standardError)
                .ConfigureAwait(false);
            return new ProcessExecutionResult(2, rootPid, default, recorder.EvidencePath, recorder);
        }

        recorder.SetRootIdentity(rootPid, rootStartTime);
        await recorder.RecordAsync(
            "process_start",
            pid: rootPid,
            startTimeUtc: rootStartTime).ConfigureAwait(false);

        using var outputCapture = new CancellationTokenSource();
        var outputTask = CaptureOutputAsync(
            process.StandardOutput,
            standardOutput,
            Console.Out,
            request.StartInfo.WorkingDirectory,
            outputCapture.Token);
        var errorTask = CaptureOutputAsync(
            process.StandardError,
            standardError,
            Console.Error,
            request.StartInfo.WorkingDirectory,
            outputCapture.Token);
        using var timeout = new CancellationTokenSource(request.Timeout);
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);

        try
        {
            await process.WaitForExitAsync(waitCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var eventName = cancellationToken.IsCancellationRequested ? "cancellation" : "timeout";
            await recorder.RecordAsync(eventName, pid: rootPid).ConfigureAwait(false);
            await StopAsync(process, request.CleanupTimeout, recorder).ConfigureAwait(false);
            await DrainOutputAsync(
                outputTask,
                errorTask,
                outputCapture,
                request.CleanupTimeout,
                recorder,
                rootPid).ConfigureAwait(false);
            await recorder.FinalizeFailureAsync(eventName, standardOutput, standardError)
                .ConfigureAwait(false);
            var exitCode = string.Equals(eventName, "timeout", StringComparison.Ordinal) ? 124 : 130;
            return new ProcessExecutionResult(
                exitCode,
                rootPid,
                rootStartTime,
                recorder.EvidencePath,
                recorder);
        }

        var streamsDrained = await DrainOutputAsync(
            outputTask,
            errorTask,
            outputCapture,
            request.CleanupTimeout,
            recorder,
            rootPid).ConfigureAwait(false);
        var processExitCode = process.ExitCode;
        await recorder.RecordAsync(
            "process_exit",
            pid: rootPid,
            exitCode: processExitCode).ConfigureAwait(false);
        if (!streamsDrained)
        {
            await recorder.FinalizeFailureAsync("stream_drain_failed", standardOutput, standardError)
                .ConfigureAwait(false);
            processExitCode = 2;
        }
        else if (processExitCode != 0)
        {
            await recorder.RecordAsync(
                "cleanup_completed",
                pid: rootPid,
                detail: "natural process exit and stream drain observed").ConfigureAwait(false);
            await recorder.FinalizeFailureAsync("process_exit", standardOutput, standardError)
                .ConfigureAwait(false);
        }
        else
        {
            recorder.SetOutputTails(standardOutput.Value, standardError.Value);
            await recorder.RecordAsync("cleanup_completed", pid: rootPid, detail: "natural process exit observed")
                .ConfigureAwait(false);
        }

        return new ProcessExecutionResult(
            processExitCode,
            rootPid,
            rootStartTime,
            recorder.EvidencePath,
            recorder);
    }

    public static async Task PreservePostExitFailureAsync(
        ProcessExecutionResult result,
        string eventName,
        string detail)
    {
        await result.Recorder.RecordAsync(
            eventName,
            pid: result.RootPid,
            detail: detail).ConfigureAwait(false);
        await result.Recorder.FinalizeFailureAsync(
            eventName,
            new TailBuffer(1),
            new TailBuffer(1)).ConfigureAwait(false);
    }

    public static Task DiscardAsync(ProcessExecutionResult result)
    {
        File.Delete(result.EvidencePath);
        return Task.CompletedTask;
    }

    private static async Task StopAsync(
        Process process,
        TimeSpan cleanupTimeout,
        FlightRecorder recorder)
    {
        await recorder.RecordAsync("bounded_stop_requested", pid: process.Id).ConfigureAwait(false);
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            using var cleanup = new CancellationTokenSource(cleanupTimeout);
            await process.WaitForExitAsync(cleanup.Token).ConfigureAwait(false);
            await recorder.RecordAsync(
                "process_exit",
                pid: process.Id,
                exitCode: process.ExitCode).ConfigureAwait(false);
            await recorder.RecordAsync("cleanup_completed", pid: process.Id).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            await recorder.RecordAsync(
                "cleanup_failed",
                pid: process.Id,
                detail: exception.Message).ConfigureAwait(false);
        }
    }

    private static async Task CaptureOutputAsync(
        StreamReader reader,
        TailBuffer tail,
        TextWriter destination,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                var redacted = RedactSensitivePaths(line, workingDirectory);
                tail.Add(redacted);
                await destination.WriteLineAsync(redacted).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The existing cleanup bound ended stream collection.
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            // Process disposal closes redirected streams after bounded cleanup.
        }
    }

    private static string RedactSensitivePaths(string value, string workingDirectory)
    {
        var redacted = ReplacePath(value, workingDirectory, "<repository-root>");
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return ReplacePath(redacted, userProfile, "<user-profile>");
    }

    private static string ReplacePath(string value, string path, string replacement)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return value;
        }

        var trimmed = Path.TrimEndingDirectorySeparator(path);
        var redacted = value.Replace(trimmed, replacement, StringComparison.OrdinalIgnoreCase);
        var alternate = trimmed.Contains('\\', StringComparison.Ordinal)
            ? trimmed.Replace('\\', '/')
            : trimmed.Replace('/', '\\');
        return redacted.Replace(alternate, replacement, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> DrainOutputAsync(
        Task outputTask,
        Task errorTask,
        CancellationTokenSource outputCapture,
        TimeSpan cleanupTimeout,
        FlightRecorder recorder,
        int rootPid)
    {
        var drain = Task.WhenAll(outputTask, errorTask);
        try
        {
            await drain.WaitAsync(cleanupTimeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            await outputCapture.CancelAsync().ConfigureAwait(false);
            await recorder.RecordAsync(
                "cleanup_failed",
                pid: rootPid,
                detail: "stdout/stderr drain exceeded the bounded cleanup window")
                .ConfigureAwait(false);
            return false;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            await recorder.RecordAsync(
                "cleanup_failed",
                pid: rootPid,
                detail: $"stdout/stderr drain failed: {exception.Message}")
                .ConfigureAwait(false);
            return false;
        }
    }
}

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

    private FlightRecorder(
        string evidencePath,
        RecorderReport report,
        TimeSpan snapshotTimeout)
    {
        EvidencePath = evidencePath;
        this.report = report;
        this.snapshotTimeout = snapshotTimeout;
    }

    public string EvidencePath { get; }

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
        var report = new RecorderReport
        {
            SliceIdentity = request.SliceIdentity,
            TestIdentity = request.TestIdentity,
            RecorderStartedAtUtc = DateTimeOffset.UtcNow,
            Events = []
        };
        var recorder = new FlightRecorder(evidencePath, report, request.CleanupTimeout);
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
        report.StdoutTail = standardOutput;
        report.StderrTail = standardError;
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
            Detail = detail
        });
        await PersistAsync().ConfigureAwait(false);
    }

    public async Task FinalizeFailureAsync(
        string outcome,
        TailBuffer standardOutput,
        TailBuffer standardError)
    {
        if (report.FinalSnapshot is not null)
        {
            return;
        }

        report.Outcome = outcome;
        if (standardOutput.Value.Length > 0 || report.StdoutTail is null)
        {
            report.StdoutTail = standardOutput.Value;
        }
        if (standardError.Value.Length > 0 || report.StderrTail is null)
        {
            report.StderrTail = standardError.Value;
        }
        var rootPid = report.RootProcess?.Pid ?? 0;
        try
        {
            report.FinalSnapshot = await ProcessTreeSnapshot.CaptureAsync(rootPid, snapshotTimeout)
                .ConfigureAwait(false);
            report.Events.Add(new RecorderEvent
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                Event = "final_snapshot",
                Pid = rootPid
            });
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            report.FinalSnapshot = new FinalProcessSnapshot
            {
                CapturedAtUtc = DateTimeOffset.UtcNow,
                Completeness = SnapshotNotice,
                Processes = [],
                Error = exception.Message
            };
            report.Events.Add(new RecorderEvent
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                Event = "final_snapshot_failed",
                Pid = rootPid,
                Detail = exception.Message
            });
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

internal sealed class TailBuffer
{
    private readonly int maximumCharacters;
    private readonly Queue<string> lines = new();
    private readonly object synchronization = new();
    private int characters;

    public TailBuffer(int maximumCharacters)
    {
        this.maximumCharacters = maximumCharacters;
    }

    public string Value
    {
        get
        {
            lock (synchronization)
            {
                return string.Join(Environment.NewLine, lines);
            }
        }
    }

    public void Add(string line)
    {
        lock (synchronization)
        {
            var retained = line.Length > maximumCharacters
                ? line[^maximumCharacters..]
                : line;
            lines.Enqueue(retained);
            characters += retained.Length + Environment.NewLine.Length;
            while (characters > maximumCharacters && lines.Count > 1)
            {
                characters -= lines.Dequeue().Length + Environment.NewLine.Length;
            }
            if (characters > maximumCharacters && lines.Count == 1)
            {
                var onlyLine = lines.Dequeue();
                var keep = Math.Max(0, maximumCharacters - Environment.NewLine.Length);
                retained = keep == 0 ? string.Empty : onlyLine[^Math.Min(keep, onlyLine.Length)..];
                lines.Enqueue(retained);
                characters = retained.Length + Environment.NewLine.Length;
            }
        }
    }
}

internal static class ProcessTreeSnapshot
{
    public static async Task<FinalProcessSnapshot> CaptureAsync(
        int rootPid,
        TimeSpan timeout)
    {
        var parentIds = await ReadParentIdsAsync(timeout).ConfigureAwait(false);
        var included = new HashSet<int>();
        if (rootPid > 0)
        {
            included.Add(rootPid);
        }

        var added = true;
        while (added)
        {
            added = false;
            foreach (var pair in parentIds)
            {
                if (included.Contains(pair.Value) && included.Add(pair.Key))
                {
                    added = true;
                }
            }
        }

        var processes = new List<ObservedProcess>();
        foreach (var pid in included.Where(parentIds.ContainsKey).Order())
        {
            DateTimeOffset? startTimeUtc = null;
            try
            {
                using var process = Process.GetProcessById(pid);
                startTimeUtc = process.StartTime.ToUniversalTime();
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // The process may exit between the point-in-time relationship snapshot and identity read.
            }

            processes.Add(new ObservedProcess
            {
                Pid = pid,
                ParentPid = parentIds[pid],
                StartTimeUtc = startTimeUtc
            });
        }

        return new FinalProcessSnapshot
        {
            CapturedAtUtc = DateTimeOffset.UtcNow,
            Completeness = FlightRecorder.SnapshotNotice,
            Processes = processes
        };
    }

    private static async Task<Dictionary<int, int>> ReadParentIdsAsync(TimeSpan timeout)
    {
        var startInfo = OperatingSystem.IsWindows()
            ? CreateWindowsSnapshotStartInfo()
            : CreateUnixSnapshotStartInfo();
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // The snapshot helper may have exited while the bound elapsed.
            }
            throw new TimeoutException("Process relationship snapshot exceeded the bounded cleanup window.");
        }
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Process relationship snapshot failed: {error.Trim()}");
        }

        var result = new Dictionary<int, int>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var values = line.Split('|', StringSplitOptions.TrimEntries);
            if (values.Length == 2 &&
                int.TryParse(values[0], out var pid) &&
                int.TryParse(values[1], out var parentPid))
            {
                result[pid] = parentPid;
            }
        }

        return result;
    }

    private static ProcessStartInfo CreateWindowsSnapshotStartInfo()
    {
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(
            "Get-CimInstance Win32_Process | ForEach-Object { '{0}|{1}' -f $_.ProcessId,$_.ParentProcessId }");
        return startInfo;
    }

    private static ProcessStartInfo CreateUnixSnapshotStartInfo()
    {
        var startInfo = new ProcessStartInfo("/bin/ps")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-axo");
        startInfo.ArgumentList.Add("pid=,ppid=");
        return startInfo;
    }
}

internal sealed class RecorderReport
{
    public required string SliceIdentity { get; init; }

    public required string TestIdentity { get; init; }

    public DateTimeOffset RecorderStartedAtUtc { get; init; }

    public RootProcessIdentity? RootProcess { get; set; }

    public string? Outcome { get; set; }

    public required List<RecorderEvent> Events { get; init; }

    public string? StdoutTail { get; set; }

    public string? StderrTail { get; set; }

    public FinalProcessSnapshot? FinalSnapshot { get; set; }

    public string? DiagnosticGuidance { get; set; }
}

internal sealed class RootProcessIdentity
{
    public int Pid { get; init; }

    public DateTimeOffset StartTimeUtc { get; init; }
}

internal sealed class RecorderEvent
{
    public DateTimeOffset TimestampUtc { get; init; }

    public required string Event { get; init; }

    public int? Pid { get; init; }

    public DateTimeOffset? StartTimeUtc { get; init; }

    public int? ExitCode { get; init; }

    public string? Detail { get; init; }
}

internal sealed class FinalProcessSnapshot
{
    public DateTimeOffset CapturedAtUtc { get; init; }

    public required string Completeness { get; init; }

    public required IReadOnlyList<ObservedProcess> Processes { get; init; }

    public string? Error { get; init; }
}

internal sealed class ObservedProcess
{
    public int Pid { get; init; }

    public int ParentPid { get; init; }

    public DateTimeOffset? StartTimeUtc { get; init; }
}
