using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace DownKyi.ProcessSupervision;

internal sealed class LinuxCgroupContainmentLease : IProcessContainmentLease
{
    private const string CgroupRoot = "/sys/fs/cgroup";

    private readonly string _directoryPath;

    private LinuxCgroupContainmentLease(
        string directoryPath,
        ProcessOwnershipMetadata metadata)
    {
        _directoryPath = directoryPath;
        Metadata = metadata;
    }

    public ProcessOwnershipMetadata Metadata { get; private set; }

    public bool MembershipRequiresAnchorExit => false;

    public static LinuxCgroupContainmentLease Prepare(Process supervisor)
    {
        ArgumentNullException.ThrowIfNull(supervisor);
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("The cgroup backend requires Linux.");
        }

        var parentMembershipId = ReadUnifiedMembershipId("/proc/self/cgroup");
        var parentDirectory = ResolveMembershipDirectory(parentMembershipId);
        var leaseName = $"downkyi-lease-{Guid.NewGuid():N}";
        var leaseDirectory = Path.Combine(parentDirectory, leaseName);
        Directory.CreateDirectory(leaseDirectory);

        try
        {
            ValidateDelegatedFiles(leaseDirectory);
            return new LinuxCgroupContainmentLease(
                leaseDirectory,
                new ProcessOwnershipMetadata(
                    ProcessIdentityAuthority.DirectChildWait,
                    ProcessContainmentKind.PosixProcessGroup,
                    ProcessContainmentStrength.DelegatedCgroupTree,
                    supervisor.Id.ToString(CultureInfo.InvariantCulture),
                    ProcessMembershipAuthority.LinuxCgroupV2,
                    CombineMembershipId(parentMembershipId, leaseName),
                    parentMembershipId,
                    RuntimeInformation.ProcessArchitecture.ToString(),
                    OwnershipEstablished: false,
                    OwnerWasAlreadyContained: false));
        }
        catch (Exception failure)
        {
            try
            {
                Directory.Delete(leaseDirectory);
            }
            catch (Exception cleanupFailure) when (
                cleanupFailure is IOException or UnauthorizedAccessException)
            {
                throw new AggregateException(
                    "Delegated cgroup preparation and rollback both failed.",
                    failure,
                    cleanupFailure);
            }

            throw;
        }
    }

    public void Establish(Process supervisor, ProcessOwnershipMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(supervisor);
        var ownershipEstablished =
            !mutation.HasFlag(ProcessOwnershipMutation.ResumeTargetBeforeOwnership) &&
            !mutation.HasFlag(ProcessOwnershipMutation.FailOwnershipEstablishment);
        if (!ownershipEstablished)
        {
            return;
        }

        File.WriteAllText(
            Path.Combine(_directoryPath, "cgroup.procs"),
            supervisor.Id.ToString(CultureInfo.InvariantCulture));
        if (mutation.HasFlag(ProcessOwnershipMutation.FailAfterMembershipAttachment))
        {
            throw new InvalidOperationException(
                "Injected failure after delegated cgroup membership attachment.");
        }

        var actualMembership = ReadUnifiedMembershipId(
            $"/proc/{supervisor.Id.ToString(CultureInfo.InvariantCulture)}/cgroup");
        if (!string.Equals(actualMembership, Metadata.MembershipId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The inert process supervisor did not enter its delegated cgroup.");
        }

        Metadata = Metadata with { OwnershipEstablished = true };
    }

    public static bool IsCurrentProcessInCgroup(string membershipId)
    {
        return string.Equals(
            ReadUnifiedMembershipId("/proc/self/cgroup"),
            membershipId,
            StringComparison.Ordinal);
    }

    public static void TerminateCgroup(string membershipId)
    {
        var directory = ResolveMembershipDirectory(membershipId);
        ValidateDelegatedFiles(directory);
        File.WriteAllText(Path.Combine(directory, "cgroup.kill"), "1");
    }

    public static void MoveCurrentProcessToCgroup(string membershipId)
    {
        var directory = ResolveMembershipDirectory(membershipId);
        File.WriteAllText(
            Path.Combine(directory, "cgroup.procs"),
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        if (!IsCurrentProcessInCgroup(membershipId))
        {
            throw new InvalidOperationException(
                "The supervisor could not retain owner lifetime outside the workload cgroup.");
        }
    }

    public bool IsTreeQuiescent()
    {
        var eventsPath = Path.Combine(_directoryPath, "cgroup.events");
        string[] lines;
        try
        {
            lines = File.ReadAllLines(eventsPath);
        }
        catch (Exception failure) when (
            failure is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "The delegated cgroup membership state is unavailable.",
                failure);
        }

        var populatedValues = lines
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 2 &&
                            string.Equals(parts[0], "populated", StringComparison.Ordinal))
            .Select(parts => parts[1])
            .ToArray();
        if (populatedValues.Length != 1 ||
            populatedValues[0] is not ("0" or "1"))
        {
            throw new InvalidOperationException(
                "The delegated cgroup membership state is malformed or ambiguous.");
        }

        return populatedValues[0] == "0";
    }

    public void Terminate()
    {
        File.WriteAllText(Path.Combine(_directoryPath, "cgroup.kill"), "1");
    }

    public void MarkAnchorReaped()
    {
    }

    public void Dispose()
    {
        if (Directory.Exists(_directoryPath))
        {
            Directory.Delete(_directoryPath);
        }
    }

    private static string ReadUnifiedMembershipId(string cgroupFile)
    {
        var entries = File.ReadAllLines(cgroupFile)
            .Where(line => line.StartsWith("0::", StringComparison.Ordinal))
            .Select(line => line[3..])
            .ToArray();
        if (entries.Length != 1 ||
            string.IsNullOrWhiteSpace(entries[0]) ||
            !entries[0].StartsWith('/'))
        {
            throw new InvalidOperationException(
                "The process is not in exactly one unified cgroup v2 hierarchy.");
        }

        return entries[0];
    }

    private static string CombineMembershipId(string parentMembershipId, string childName)
    {
        return parentMembershipId == "/"
            ? $"/{childName}"
            : $"{parentMembershipId.TrimEnd('/')}/{childName}";
    }

    internal static string ResolveMembershipDirectory(string membershipId)
    {
        if (string.IsNullOrWhiteSpace(membershipId) ||
            !membershipId.StartsWith('/') ||
            membershipId.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The cgroup membership identity is invalid.");
        }

        var fullPath = Path.GetFullPath(
            Path.Combine(CgroupRoot, membershipId.TrimStart('/')));
        var authorityRoot = Path.GetFullPath(CgroupRoot);
        var rootPrefix = authorityRoot + Path.DirectorySeparatorChar;
        if (!string.Equals(fullPath, authorityRoot, StringComparison.Ordinal) &&
            !fullPath.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The cgroup membership identity escaped its authority root.");
        }

        return fullPath;
    }

    private static void ValidateDelegatedFiles(string directory)
    {
        foreach (var fileName in new[] { "cgroup.events", "cgroup.procs", "cgroup.kill" })
        {
            if (!File.Exists(Path.Combine(directory, fileName)))
            {
                throw new InvalidOperationException(
                    $"The delegated cgroup does not expose required file '{fileName}'.");
            }
        }
    }

    internal static string ResolveCurrentMembershipDirectory()
    {
        return ResolveMembershipDirectory(ReadUnifiedMembershipId("/proc/self/cgroup"));
    }
}
