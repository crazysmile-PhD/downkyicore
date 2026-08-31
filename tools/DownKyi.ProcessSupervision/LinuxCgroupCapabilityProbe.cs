using System.Runtime.InteropServices;

namespace DownKyi.ProcessSupervision;

internal enum LinuxCgroupAuthorityPresence
{
    Present,
    Missing,
    Ambiguous
}

internal sealed record LinuxCgroupProbeSnapshot(
    IReadOnlyList<string> MembershipLines,
    LinuxCgroupAuthorityPresence MountContract,
    LinuxCgroupAuthorityPresence MembershipDirectory,
    LinuxCgroupAuthorityPresence ProcessMembershipFile,
    LinuxCgroupAuthorityPresence OccupancyFile,
    LinuxCgroupAuthorityPresence KillFile,
    string? ResolvedMembershipDirectory,
    int AccessResult,
    int AccessError);

internal static partial class LinuxCgroupCapabilityProbe
{
    private const string CgroupRoot = "/sys/fs/cgroup";
    private const int WriteAccess = 2;
    private const int ExecuteAccess = 1;
    private const int PermissionDenied = 13;
    private const int ReadOnlyFileSystem = 30;

    public static LinuxCgroupCapability Probe()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Linux cgroup probing requires Linux.");
        }

        string[] membershipLines;
        try
        {
            membershipLines = File.ReadAllLines("/proc/self/cgroup");
        }
        catch (Exception failure) when (
            failure is IOException or UnauthorizedAccessException)
        {
            return LinuxCgroupCapability.Ambiguous(
                $"The unified cgroup membership could not be inspected: {failure.GetType().Name}.");
        }

        var entries = ExtractUnifiedEntries(membershipLines);
        if (entries.Length == 0)
        {
            return LinuxCgroupCapability.DefinitelyUnavailable(
                "The process has no unified cgroup v2 membership.");
        }

        string? directory = null;
        LinuxCgroupAuthorityPresence directoryPresence;
        LinuxCgroupAuthorityPresence processMembershipPresence;
        LinuxCgroupAuthorityPresence occupancyPresence;
        LinuxCgroupAuthorityPresence killPresence;
        if (entries.Length == 1 &&
            !string.IsNullOrWhiteSpace(entries[0]) &&
            entries[0].StartsWith('/'))
        {
            try
            {
                directory = ResolveMembershipDirectory(entries[0]);
                directoryPresence = InspectAuthority(directory);
                processMembershipPresence = InspectAuthority(
                    Path.Combine(directory, "cgroup.procs"));
                occupancyPresence = InspectAuthority(
                    Path.Combine(directory, "cgroup.events"));
                killPresence = InspectAuthority(
                    Path.Combine(directory, "cgroup.kill"));
            }
            catch (ContainmentAuthorityException)
            {
                directoryPresence = LinuxCgroupAuthorityPresence.Ambiguous;
                processMembershipPresence = LinuxCgroupAuthorityPresence.Ambiguous;
                occupancyPresence = LinuxCgroupAuthorityPresence.Ambiguous;
                killPresence = LinuxCgroupAuthorityPresence.Ambiguous;
            }
        }
        else
        {
            directoryPresence = LinuxCgroupAuthorityPresence.Ambiguous;
            processMembershipPresence = LinuxCgroupAuthorityPresence.Ambiguous;
            occupancyPresence = LinuxCgroupAuthorityPresence.Ambiguous;
            killPresence = LinuxCgroupAuthorityPresence.Ambiguous;
        }

        var mountPresence = InspectAuthority(Path.Combine(CgroupRoot, "cgroup.controllers"));
        var accessResult = -1;
        var accessError = 0;
        if (directoryPresence == LinuxCgroupAuthorityPresence.Present)
        {
            Marshal.SetLastPInvokeError(0);
            accessResult = Access(directory!, WriteAccess | ExecuteAccess);
            accessError = accessResult == 0 ? 0 : Marshal.GetLastPInvokeError();
        }

        return Classify(new LinuxCgroupProbeSnapshot(
            membershipLines,
            mountPresence,
            directoryPresence,
            processMembershipPresence,
            occupancyPresence,
            killPresence,
            directory,
            accessResult,
            accessError));
    }

    internal static LinuxCgroupCapability ClassifyForTesting(
        LinuxCgroupProbeSnapshot snapshot)
    {
        return Classify(snapshot);
    }

    internal static string ParseUnifiedMembership(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var entries = ExtractUnifiedEntries(lines);
        if (entries.Length != 1 ||
            string.IsNullOrWhiteSpace(entries[0]) ||
            !entries[0].StartsWith('/'))
        {
            throw new ContainmentAuthorityException(
                ContainmentAuthorityFailureKind.MembershipAmbiguous,
                "The process is not in exactly one unified cgroup v2 hierarchy.");
        }

        return entries[0];
    }

    private static LinuxCgroupCapability Classify(LinuxCgroupProbeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var entries = ExtractUnifiedEntries(snapshot.MembershipLines);
        if (entries.Length == 0)
        {
            return LinuxCgroupCapability.DefinitelyUnavailable(
                "The process has no unified cgroup v2 membership.");
        }

        if (entries.Length != 1 ||
            string.IsNullOrWhiteSpace(entries[0]) ||
            !entries[0].StartsWith('/'))
        {
            return LinuxCgroupCapability.Ambiguous(
                "The unified cgroup membership is malformed or ambiguous.");
        }

        if (snapshot.MountContract == LinuxCgroupAuthorityPresence.Missing ||
            snapshot.MembershipDirectory == LinuxCgroupAuthorityPresence.Missing ||
            snapshot.ProcessMembershipFile == LinuxCgroupAuthorityPresence.Missing ||
            snapshot.OccupancyFile == LinuxCgroupAuthorityPresence.Missing ||
            snapshot.KillFile == LinuxCgroupAuthorityPresence.Missing)
        {
            return LinuxCgroupCapability.DefinitelyUnavailable(
                "The kernel does not expose the required cgroup v2 containment authority.");
        }

        if (snapshot.MountContract == LinuxCgroupAuthorityPresence.Ambiguous ||
            snapshot.MembershipDirectory == LinuxCgroupAuthorityPresence.Ambiguous ||
            snapshot.ProcessMembershipFile == LinuxCgroupAuthorityPresence.Ambiguous ||
            snapshot.OccupancyFile == LinuxCgroupAuthorityPresence.Ambiguous ||
            snapshot.KillFile == LinuxCgroupAuthorityPresence.Ambiguous)
        {
            return LinuxCgroupCapability.Ambiguous(
                "The unified cgroup authority could not be inspected unambiguously.");
        }

        if (string.IsNullOrWhiteSpace(snapshot.ResolvedMembershipDirectory))
        {
            return LinuxCgroupCapability.Ambiguous(
                "The unified cgroup membership directory is unavailable.");
        }

        if (snapshot.AccessResult == 0)
        {
            return new LinuxCgroupCapability(
                LinuxCgroupAvailability.WritableDelegation,
                entries[0],
                snapshot.ResolvedMembershipDirectory,
                null);
        }

        return snapshot.AccessError is PermissionDenied or ReadOnlyFileSystem
            ? LinuxCgroupCapability.DefinitelyUnavailable(
                "The current unified cgroup is not delegated as writable.")
            : LinuxCgroupCapability.Ambiguous(
                $"The current unified cgroup access probe failed with errno {snapshot.AccessError}.");
    }

    private static string[] ExtractUnifiedEntries(IEnumerable<string> lines)
    {
        return lines
            .Where(line => line.StartsWith("0::", StringComparison.Ordinal))
            .Select(line => line[3..])
            .ToArray();
    }

    private static LinuxCgroupAuthorityPresence InspectAuthority(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return LinuxCgroupAuthorityPresence.Present;
        }
        catch (Exception failure) when (
            failure is FileNotFoundException or DirectoryNotFoundException)
        {
            return LinuxCgroupAuthorityPresence.Missing;
        }
        catch (Exception failure) when (
            failure is IOException or UnauthorizedAccessException)
        {
            return LinuxCgroupAuthorityPresence.Ambiguous;
        }
    }

    internal static string ResolveMembershipDirectory(string membershipId)
    {
        if (string.IsNullOrWhiteSpace(membershipId) ||
            !membershipId.StartsWith('/') ||
            membershipId.Contains("..", StringComparison.Ordinal))
        {
            throw new ContainmentAuthorityException(
                ContainmentAuthorityFailureKind.MembershipAmbiguous,
                "The cgroup membership identity is invalid.");
        }

        var authorityRoot = Path.GetFullPath(CgroupRoot);
        var fullPath = Path.GetFullPath(
            Path.Combine(authorityRoot, membershipId.TrimStart('/')));
        if (!string.Equals(fullPath, authorityRoot, StringComparison.Ordinal) &&
            !fullPath.StartsWith(
                authorityRoot + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            throw new ContainmentAuthorityException(
                ContainmentAuthorityFailureKind.MembershipAmbiguous,
                "The cgroup membership identity escaped its authority root.");
        }

        return fullPath;
    }

    [LibraryImport("libc", EntryPoint = "access", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int Access(string path, int mode);
}
