using System.Globalization;

namespace DownKyi.CentralTestRunner;

// The host is still waiting for its launch request when it enters this cgroup.
// No workload can run before the kernel has accepted the membership change.
internal sealed class LinuxCgroupContainment : IDisposable
{
    private readonly string path;

    private LinuxCgroupContainment(string path)
    {
        this.path = path;
    }

    internal static LinuxCgroupContainment? TryCreateForHost(int hostPid)
    {
        var membership = File.ReadLines("/proc/self/cgroup")
            .FirstOrDefault(line => line.StartsWith("0::", StringComparison.Ordinal));
        if (membership is null)
        {
            return null; // cgroup v2 is unavailable.
        }

        var relative = membership[3..].TrimStart('/');
        var parent = Path.GetFullPath(Path.Combine("/sys/fs/cgroup", relative));
        if (!File.Exists(Path.Combine(parent, "cgroup.procs")) ||
            !File.Exists(Path.Combine(parent, "cgroup.events")))
        {
            return null;
        }

        var path = Path.Combine(parent, $"downkyi-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return null; // The runner has no delegated writable cgroup subtree.
        }

        try
        {
            if (!File.Exists(Path.Combine(path, "cgroup.kill")))
            {
                Directory.Delete(path);
                return null; // This kernel cannot provide authoritative group kill.
            }

            File.WriteAllText(Path.Combine(path, "cgroup.procs"),
                hostPid.ToString(CultureInfo.InvariantCulture));
            var actual = File.ReadLines($"/proc/{hostPid}/cgroup")
                .FirstOrDefault(line => line.StartsWith("0::", StringComparison.Ordinal));
            if (actual is null || !actual[3..].EndsWith("/" + Path.GetFileName(path), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The ownership host did not enter its cgroup.");
            }

            return new LinuxCgroupContainment(path);
        }
        catch
        {
            // Membership may already have changed. Never downgrade to a process group
            // after this point; the launch must fail closed and the host must be reaped.
            try { File.WriteAllText(Path.Combine(path, "cgroup.kill"), "1"); }
            catch (IOException) { }
            throw;
        }
    }

    internal void Kill() => File.WriteAllText(Path.Combine(path, "cgroup.kill"), "1");

    internal bool IsQuiescent()
    {
        var populated = File.ReadLines(Path.Combine(path, "cgroup.events"))
            .FirstOrDefault(line => line.StartsWith("populated ", StringComparison.Ordinal));
        return populated switch
        {
            "populated 0" => true,
            "populated 1" => false,
            _ => throw new InvalidOperationException("The owned cgroup has no valid populated state.")
        };
    }

    internal string? ReadLiveEvidence(CleanupDeadline? deadline = null)
    {
        foreach (var line in File.ReadLines(Path.Combine(path, "cgroup.procs")))
        {
            if (deadline?.WorkWindow == TimeSpan.Zero)
            {
                throw new TimeoutException("Owned cgroup observation exceeded the cleanup deadline.");
            }
            if (!int.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
            {
                throw new InvalidOperationException("The owned cgroup returned an invalid process ID.");
            }

            try
            {
                var stat = File.ReadAllText($"/proc/{pid}/stat");
                var nameEnd = stat.LastIndexOf(')');
                if (nameEnd < 0 || nameEnd + 2 >= stat.Length)
                {
                    throw new InvalidOperationException($"Invalid /proc state for pid={pid}.");
                }
                var state = stat[nameEnd + 2];
                if (state is not ('Z' or 'X'))
                {
                    return $"pid={pid}, state={state}";
                }
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                // The member exited between cgroup and /proc observations.
            }
        }

        return null;
    }

    public void Dispose()
    {
        try { Directory.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A failed cleanup is reported by quiescence, not disposal.
        }
    }
}
