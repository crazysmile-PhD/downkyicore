using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;

namespace DownKyi.CentralTestRunner;

// This observes the group established at launch. It never authorizes a signal
// from an observed PID, and never starts a helper process or background task.
internal static class UnixProcessGroupInspector
{
    internal static Dictionary<int, int> ReadParentIds(CleanupDeadline deadline) => OperatingSystem.IsLinux()
        ? ReadLinuxParentIds(deadline)
        : OperatingSystem.IsMacOS()
            ? ReadMacParentIds(deadline)
            : throw new PlatformNotSupportedException();

    internal static string? ReadLiveMember(
        int groupId, int? ignoredPid = null, CleanupDeadline? deadline = null)
    {
        return OperatingSystem.IsLinux()
            ? ReadLinuxLiveMember(groupId, ignoredPid, deadline)
            : OperatingSystem.IsMacOS()
                ? ReadMacLiveMember(groupId, ignoredPid, deadline)
                : throw new PlatformNotSupportedException();
    }

    private static string? ReadLinuxLiveMember(
        int groupId, int? ignoredPid, CleanupDeadline? deadline)
    {
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            RequireBudget(deadline);
            if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var pid))
            {
                continue;
            }
            if (pid == ignoredPid)
            {
                continue;
            }

            string stat;
            try { stat = File.ReadAllText(Path.Combine(directory, "stat")); }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                continue; // The process exited during the observation.
            }

            var nameEnd = stat.LastIndexOf(')');
            if (nameEnd < 0 || nameEnd + 2 >= stat.Length)
            {
                throw new InvalidOperationException($"Invalid /proc state for pid={pid}.");
            }

            var fields = stat[(nameEnd + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3 || !int.TryParse(fields[2], NumberStyles.None,
                    CultureInfo.InvariantCulture, out var processGroup))
            {
                throw new InvalidOperationException($"Invalid /proc group for pid={pid}.");
            }

            if (processGroup == groupId && fields[0][0] is not ('Z' or 'X'))
            {
                return $"pid={pid}, state={fields[0]}";
            }
        }

        return null;
    }

    private static Dictionary<int, int> ReadLinuxParentIds(CleanupDeadline deadline)
    {
        var parents = new Dictionary<int, int>();
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (deadline.Remaining == TimeSpan.Zero)
            {
                throw new TimeoutException("Process relationship snapshot exceeded its window.");
            }
            if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var pid))
            {
                continue;
            }

            string stat;
            try { stat = File.ReadAllText(Path.Combine(directory, "stat")); }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                continue;
            }

            var nameEnd = stat.LastIndexOf(')');
            var fields = nameEnd < 0 ? [] : stat[(nameEnd + 2)..]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3 || !int.TryParse(fields[1], NumberStyles.None,
                    CultureInfo.InvariantCulture, out var parent))
            {
                throw new InvalidOperationException($"Invalid /proc parent for pid={pid}.");
            }

            parents[pid] = parent;
        }

        return parents;
    }

    private static Dictionary<int, int> ReadMacParentIds(CleanupDeadline deadline)
    {
        var capacity = 256;
        for (var attempt = 0; attempt < 7; attempt++, capacity *= 2)
        {
            if (deadline.Remaining == TimeSpan.Zero)
            {
                throw new TimeoutException("Process relationship snapshot exceeded its window.");
            }
            var buffer = Marshal.AllocHGlobal(capacity * sizeof(int));
            try
            {
                Marshal.SetLastPInvokeError(0);
                var count = NativeMethods.ListAllPids(buffer, capacity * sizeof(int));
                if (count < 0 || (count == 0 && Marshal.GetLastPInvokeError() != 0))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(),
                        "macOS process relationship observation failed.");
                }
                if (count >= capacity)
                {
                    continue;
                }

                var parents = new Dictionary<int, int>();
                for (var index = 0; index < count; index++)
                {
                    if (deadline.Remaining == TimeSpan.Zero)
                    {
                        throw new TimeoutException("Process relationship snapshot exceeded its window.");
                    }
                    var pid = Marshal.ReadInt32(buffer, index * sizeof(int));
                    var info = Marshal.AllocHGlobal(64);
                    try
                    {
                        Marshal.SetLastPInvokeError(0);
                        var bytes = NativeMethods.GetProcessInfo(pid, 13, 0, info, 64);
                        if (bytes == 0 && Marshal.GetLastPInvokeError() == 3)
                        {
                            continue;
                        }
                        if (bytes < 16)
                        {
                            throw new Win32Exception(Marshal.GetLastPInvokeError(),
                                $"macOS relationship observation failed for pid={pid}.");
                        }

                        parents[pid] = Marshal.ReadInt32(info, 4);
                    }
                    finally { Marshal.FreeHGlobal(info); }
                }

                return parents;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        throw new InvalidOperationException("macOS relationship observation exceeded its bounded buffer.");
    }

    private static string? ReadMacLiveMember(
        int groupId, int? ignoredPid, CleanupDeadline? deadline)
    {
        var capacity = 64;
        for (var attempt = 0; attempt < 6; attempt++, capacity *= 2)
        {
            RequireBudget(deadline);
            var buffer = Marshal.AllocHGlobal(capacity * sizeof(int));
            try
            {
                Marshal.SetLastPInvokeError(0);
                var count = NativeMethods.ListProcessGroupPids(groupId, buffer, capacity * sizeof(int));
                if (count < 0 || (count == 0 && Marshal.GetLastPInvokeError() != 0))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(),
                        "macOS process-group observation failed.");
                }
                if (count >= capacity)
                {
                    continue;
                }

                for (var index = 0; index < count; index++)
                {
                    RequireBudget(deadline);
                    var pid = Marshal.ReadInt32(buffer, index * sizeof(int));
                    if (pid == ignoredPid)
                    {
                        continue;
                    }
                    var info = Marshal.AllocHGlobal(64); // proc_bsdshortinfo
                    try
                    {
                        Marshal.SetLastPInvokeError(0);
                        var bytes = NativeMethods.GetProcessInfo(pid, 13, 0, info, 64);
                        if (bytes == 0 && Marshal.GetLastPInvokeError() == 3)
                        {
                            continue; // Gone between membership and state observation.
                        }
                        if (bytes < 16)
                        {
                            throw new Win32Exception(Marshal.GetLastPInvokeError(),
                                $"macOS state observation failed for pid={pid}.");
                        }

                        var observedGroup = Marshal.ReadInt32(info, 8);
                        var state = Marshal.ReadInt32(info, 12);
                        if (observedGroup == groupId && state != 5) // SZOMB
                        {
                            return $"pid={pid}, state={state}";
                        }
                    }
                    finally { Marshal.FreeHGlobal(info); }
                }

                return null;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        throw new InvalidOperationException("macOS process-group observation exceeded its bounded buffer.");
    }

    private static void RequireBudget(CleanupDeadline? deadline)
    {
        if (deadline is not null && deadline.WorkWindow == TimeSpan.Zero)
        {
            throw new TimeoutException("Owned process-group observation exceeded the cleanup deadline.");
        }
    }

    private static class NativeMethods
    {
        [DllImport("/usr/lib/libproc.dylib", EntryPoint = "proc_listallpids", SetLastError = true)]
        internal static extern int ListAllPids(IntPtr buffer, int size);

        [DllImport("/usr/lib/libproc.dylib", EntryPoint = "proc_listpgrppids", SetLastError = true)]
        internal static extern int ListProcessGroupPids(int groupId, IntPtr buffer, int size);

        [DllImport("/usr/lib/libproc.dylib", EntryPoint = "proc_pidinfo", SetLastError = true)]
        internal static extern int GetProcessInfo(int pid, int flavor, ulong argument, IntPtr buffer, int size);
    }
}
