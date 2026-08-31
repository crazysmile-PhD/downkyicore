namespace DownKyi.ProcessSupervision;

internal static class PlatformProcessContainmentRouter
{
    private static readonly IProcessContainmentBackend Windows =
        new WindowsJobContainmentBackend();
    private static readonly IProcessContainmentBackend LinuxCgroup =
        new LinuxCgroupContainmentBackend();
    private static readonly IProcessContainmentBackend LinuxProcessGroup =
        new LinuxProcessGroupContainmentBackend();
    private static readonly IProcessContainmentBackend MacProcessGroup =
        new MacProcessGroupContainmentBackend();

    public static IProcessContainmentBackend Select(
        PlatformContainmentFacts facts,
        ProcessContainmentRequirement requirement)
    {
        var backend = facts.Platform switch
        {
            ProcessContainmentPlatform.Windows => Windows,
            ProcessContainmentPlatform.MacOS => MacProcessGroup,
            ProcessContainmentPlatform.Linux => SelectLinux(facts.LinuxCgroup),
            _ => throw new ContainmentAuthorityException(
                ContainmentAuthorityFailureKind.UnsupportedPlatform,
                "No authoritative process-containment backend exists for this platform.")
        };

        if (requirement == ProcessContainmentRequirement.RequireStrongContainment &&
            backend.Kind is not (ProcessContainmentBackendKind.WindowsJob or
                ProcessContainmentBackendKind.LinuxDelegatedCgroup))
        {
            throw new ContainmentAuthorityException(
                ContainmentAuthorityFailureKind.AuthorityUnavailable,
                "The selected platform does not expose the required strong tree containment.");
        }

        return backend;
    }

    public static IProcessContainmentBackend SelectEstablished(
        ContainmentAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        return attachment.BackendKind switch
        {
            ProcessContainmentBackendKind.WindowsJob => Windows,
            ProcessContainmentBackendKind.LinuxDelegatedCgroup => LinuxCgroup,
            ProcessContainmentBackendKind.LinuxProcessGroup => LinuxProcessGroup,
            ProcessContainmentBackendKind.MacProcessGroup => MacProcessGroup,
            _ => throw new ContainmentAuthorityException(
                ContainmentAuthorityFailureKind.MembershipAmbiguous,
                "The containment attachment names an unknown backend.")
        };
    }

    public static PlatformContainmentFacts CapturePlatformFacts()
    {
        if (OperatingSystem.IsWindows())
        {
            return new PlatformContainmentFacts(
                ProcessContainmentPlatform.Windows,
                LinuxCgroupCapability.DefinitelyUnavailable("The host is not Linux."));
        }

        if (OperatingSystem.IsLinux())
        {
            return new PlatformContainmentFacts(
                ProcessContainmentPlatform.Linux,
                LinuxCgroupCapabilityProbe.Probe());
        }

        if (OperatingSystem.IsMacOS())
        {
            return new PlatformContainmentFacts(
                ProcessContainmentPlatform.MacOS,
                LinuxCgroupCapability.DefinitelyUnavailable("The host is not Linux."));
        }

        return new PlatformContainmentFacts(
            ProcessContainmentPlatform.Unsupported,
            LinuxCgroupCapability.DefinitelyUnavailable("The host platform is unsupported."));
    }

    private static IProcessContainmentBackend SelectLinux(
        LinuxCgroupCapability capability)
    {
        return capability.Availability switch
        {
            LinuxCgroupAvailability.WritableDelegation => LinuxCgroup,
            LinuxCgroupAvailability.DefinitelyUnavailable => LinuxProcessGroup,
            _ => throw new ContainmentAuthorityException(
                ContainmentAuthorityFailureKind.AmbiguousCapability,
                capability.Detail ?? "Linux cgroup delegation is ambiguous.")
        };
    }
}
