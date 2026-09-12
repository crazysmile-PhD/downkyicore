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
        CleanupDeadline deadline,
        Func<ObservedProcess, ProcessIdentityState>? observeIdentity = null,
        Action<int>? signalProcess = null)
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
        var unresolved = new List<ObservedProcess>();
        var observe = observeIdentity ?? (process => ObservedProcessIdentity.Observe(
            process, onSame: current => current.Kill()));
        var signal = signalProcess ?? Signal;
        foreach (var descendant in captured
                     .Where(process => process.Pid != root.Id)
                     .OrderByDescending(process => Depth(process.Pid, parentByPid)))
        {
            if (deadline.Remaining == TimeSpan.Zero)
            {
                failures.Add("termination deadline expired before all captured descendants were signaled");
                break;
            }

            ProcessIdentityState identity;
            try
            {
                identity = observe(descendant);
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
                if (ObservedProcessIdentity.Observe(descendant) != ProcessIdentityState.Gone)
                {
                    failures.Add($"pid {descendant.Pid}: {exception.Message}");
                }

                continue;
            }
            if (identity == ProcessIdentityState.Unknown)
            {
                unresolved.Add(descendant);
                continue;
            }

            if (identity == ProcessIdentityState.Same && observeIdentity is not null)
            {
                try
                {
                    signal(descendant.Pid);
                }
                catch (Win32Exception exception)
                {
                    failures.Add($"pid {descendant.Pid}: {exception.Message}");
                }
            }
        }

        if (!root.HasExited)
        {
            try
            {
                signal(root.Id);
            }
            catch (Win32Exception exception)
            {
                failures.Add($"root pid {root.Id}: {exception.Message}");
            }
        }

        foreach (var descendant in unresolved)
        {
            ProcessIdentityState identity;
            try
            {
                identity = await ObservedProcessIdentity.ResolveUnknownAsync(
                    descendant, deadline, observe).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
                if (ObservedProcessIdentity.Observe(descendant) != ProcessIdentityState.Gone)
                {
                    failures.Add($"pid {descendant.Pid}: {exception.Message}");
                }

                continue;
            }
            if (identity == ProcessIdentityState.Unknown)
            {
                failures.Add($"pid {descendant.Pid}: identity remained unknown until the cleanup deadline");
            }
            else if (identity == ProcessIdentityState.Same && observeIdentity is not null)
            {
                try
                {
                    signal(descendant.Pid);
                }
                catch (Win32Exception exception)
                {
                    failures.Add($"pid {descendant.Pid}: {exception.Message}");
                }
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
