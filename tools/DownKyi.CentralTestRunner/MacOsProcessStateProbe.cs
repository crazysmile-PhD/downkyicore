using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace DownKyi.CentralTestRunner;

internal sealed record MacOsProcessStateProbeResult(
    int TargetPid,
    int? ParentPid,
    string RawState,
    string CanonicalState,
    int? ExitCode,
    string Result)
{
    internal string ToDiagnosticDetail()
    {
        return $"targetPid={TargetPid} " +
            $"ppid={ParentPid?.ToString(CultureInfo.InvariantCulture) ?? "unavailable"} " +
            $"rawState={(string.IsNullOrEmpty(RawState) ? "unavailable" : RawState)} " +
            $"canonicalState={CanonicalState} " +
            $"exitCode={ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unavailable"} " +
            $"result={Result}";
    }
}

internal static class MacOsProcessStateProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(500);

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Failure-only diagnostics must never replace the original process cleanup failure.")]
    internal static async Task<MacOsProcessStateProbeResult> ProbeAsync(int processId)
    {
        try
        {
            var startInfo = new ProcessStartInfo("/bin/ps")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add("pid=");
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add("ppid=");
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add("state=");
            startInfo.ArgumentList.Add("-p");
            startInfo.ArgumentList.Add(processId.ToString(CultureInfo.InvariantCulture));

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(ProbeTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                TryTerminateProbe(process);
                return Failure(processId, "timeout");
            }

            await Task.WhenAll(outputTask, errorTask).WaitAsync(ProbeTimeout).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            return Parse(processId, output, process.ExitCode);
        }
        catch (Exception exception)
        {
            return Failure(processId, "probe-exception", exception.GetType().FullName);
        }
    }

    internal static MacOsProcessStateProbeResult Parse(int targetPid, string output, int exitCode)
    {
        if (exitCode != 0)
        {
            return Failure(targetPid, "nonzero-exit", exitCode: exitCode);
        }

        var fields = output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 3 ||
            !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var observedPid) ||
            observedPid != targetPid ||
            !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parentPid))
        {
            return Failure(targetPid, "unparseable-output", exitCode: exitCode);
        }

        var rawState = fields[2];
        return new MacOsProcessStateProbeResult(
            targetPid,
            parentPid,
            rawState,
            Canonicalize(rawState),
            exitCode,
            "success");
    }

    private static string Canonicalize(string rawState)
    {
        if (string.IsNullOrEmpty(rawState))
        {
            return "unknown";
        }

        return char.ToUpperInvariant(rawState[0]) switch
        {
            'R' => "runnable",
            'S' => "sleeping",
            'I' => "idle",
            'T' => "stopped",
            'U' => "uninterruptible-wait",
            'Z' => "zombie",
            'X' => "dead",
            _ => "unknown",
        };
    }

    private static MacOsProcessStateProbeResult Failure(
        int targetPid,
        string result,
        string? exceptionType = null,
        int? exitCode = null)
    {
        return new MacOsProcessStateProbeResult(
            targetPid,
            null,
            string.Empty,
            "unavailable",
            exitCode,
            exceptionType is null ? result : $"{result}:{exceptionType}");
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Best-effort teardown keeps the diagnostic probe bounded without replacing the cleanup failure.")]
    private static void TryTerminateProbe(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (Exception)
        {
            // The original process cleanup failure remains authoritative.
        }
    }
}
