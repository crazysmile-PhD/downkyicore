using System.Diagnostics;
using System.Globalization;

namespace DownKyi.CentralTestRunner;

internal static class ProcessTreeSnapshot
{
    public static FinalProcessSnapshot Capture(
        int rootPid,
        TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new TimeoutException("Process relationship snapshot exceeded the bounded cleanup window.");
        }

        var deadline = new CleanupDeadline(timeout);
        var parentIds = OperatingSystem.IsWindows()
            ? WindowsProcessRelationshipSnapshot.ReadParentIds()
            : UnixProcessGroupInspector.ReadParentIds(deadline);
        if (deadline.Remaining == TimeSpan.Zero)
        {
            throw new TimeoutException("Process relationship snapshot exceeded the bounded cleanup window.");
        }
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

}
