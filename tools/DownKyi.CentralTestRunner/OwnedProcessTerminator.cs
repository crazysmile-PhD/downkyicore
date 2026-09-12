using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DownKyi.CentralTestRunner;

internal static class OwnedProcessTerminator
{
    private const int SigKill = 9;
    private const int NoSuchProcess = 3;

    internal static async Task<IReadOnlyList<string>> TerminateAsync(
        Process root,
        IReadOnlyList<ObservedProcess>? capturedProcesses,
        CleanupDeadline deadline)
    {
        if (OperatingSystem.IsWindows())
        {
            await Task.Run(() =>
            {
                if (!root.HasExited)
                {
                    root.Kill(entireProcessTree: true);
                }
            }).WaitAsync(deadline.Remaining).ConfigureAwait(false);
            return [];
        }

        var failures = new List<string>();
        var captured = capturedProcesses ?? [];
        var parentByPid = captured.ToDictionary(process => process.Pid, process => process.ParentPid);
        foreach (var descendant in captured
                     .Where(process => process.Pid != root.Id)
                     .OrderByDescending(process => Depth(process.Pid, parentByPid)))
        {
            if (deadline.Remaining == TimeSpan.Zero)
            {
                failures.Add("termination deadline expired before all captured descendants were signaled");
                break;
            }

            try
            {
                using var process = Process.GetProcessById(descendant.Pid);
                if (descendant.StartTimeUtc is null)
                {
                    failures.Add($"pid {descendant.Pid} has no verified start time; signal omitted");
                    continue;
                }

                if (process.StartTime.ToUniversalTime() != descendant.StartTimeUtc.Value.UtcDateTime)
                {
                    continue;
                }

                Signal(descendant.Pid);
            }
            catch (ArgumentException)
            {
                // The captured PID disappeared before the signal.
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
                failures.Add($"pid {descendant.Pid}: {exception.Message}");
            }
        }

        if (!root.HasExited)
        {
            try
            {
                Signal(root.Id);
            }
            catch (Win32Exception exception)
            {
                failures.Add($"root pid {root.Id}: {exception.Message}");
            }
        }

        return failures;
    }

    private static int Depth(int pid, Dictionary<int, int> parentByPid)
    {
        var visited = new HashSet<int>();
        var depth = 0;
        while (parentByPid.TryGetValue(pid, out var parentPid) && visited.Add(pid))
        {
            depth++;
            pid = parentPid;
        }

        return depth;
    }

    private static void Signal(int pid)
    {
        if (NativeMethods.Kill(pid, SigKill) == 0 || Marshal.GetLastPInvokeError() == NoSuchProcess)
        {
            return;
        }

        throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    private static class NativeMethods
    {
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
        internal static extern int Kill(int pid, int signal);
    }
}
