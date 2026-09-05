using System.Diagnostics;
using System.Globalization;

namespace DownKyi.CentralTestRunner;

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

        return ParseParentIds(output);
    }

    internal static Dictionary<int, int> ParseParentIds(string output)
    {
        var result = new Dictionary<int, int>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var values = line.Contains('|', StringComparison.Ordinal)
                ? line.Split('|', StringSplitOptions.TrimEntries)
                : line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (values.Length == 2 &&
                int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) &&
                int.TryParse(values[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parentPid) &&
                pid > 0 &&
                parentPid >= 0)
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
