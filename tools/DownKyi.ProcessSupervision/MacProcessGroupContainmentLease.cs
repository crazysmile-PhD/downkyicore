using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace DownKyi.ProcessSupervision;

internal sealed partial class MacProcessGroupContainmentLease : IProcessContainmentLease
{
    private readonly int _anchorProcessId;
    private bool _anchorReaped;

    private MacProcessGroupContainmentLease(ProcessOwnershipMetadata metadata)
    {
        Metadata = metadata;
        _anchorProcessId = int.Parse(
            metadata.ContainmentId,
            NumberStyles.None,
            CultureInfo.InvariantCulture);
    }

    public ProcessOwnershipMetadata Metadata { get; private set; }

    public bool MembershipRequiresAnchorExit => false;

    public static MacProcessGroupContainmentLease Prepare(Process supervisor)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("The libproc backend requires macOS.");
        }

        var identity = supervisor.Id.ToString(CultureInfo.InvariantCulture);
        return new MacProcessGroupContainmentLease(
            new ProcessOwnershipMetadata(
                ProcessIdentityAuthority.DirectChildWait,
                ProcessContainmentKind.PosixProcessGroup,
                ProcessContainmentStrength.TrustedChildProcessGroup,
                identity,
                ProcessMembershipAuthority.MacOSLibprocProcessGroup,
                identity,
                identity,
                RuntimeInformation.ProcessArchitecture.ToString(),
                OwnershipEstablished: false,
                OwnerWasAlreadyContained: false));
    }

    public void Establish(Process supervisor, ProcessOwnershipMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(supervisor);
        Metadata = Metadata with
        {
            OwnershipEstablished =
                !mutation.HasFlag(ProcessOwnershipMutation.FailOwnershipEstablishment)
        };
    }

    public static bool ContainsProcess(int processGroupId, int processId)
    {
        return QueryMembers(processGroupId).Contains(processId);
    }

    public bool IsTreeQuiescent()
    {
        EnsureAnchorRetained();
        return QueryMembers(_anchorProcessId)
            .All(processId => processId == _anchorProcessId);
    }

    public void Terminate()
    {
        EnsureAnchorRetained();
        PosixProcessGroupTermination.Terminate(_anchorProcessId);
    }

    public void MarkAnchorReaped()
    {
        _anchorReaped = true;
    }

    public void Dispose()
    {
    }

    private void EnsureAnchorRetained()
    {
        if (_anchorReaped)
        {
            throw new InvalidOperationException(
                "The macOS process-group authority was queried after its anchor was reaped.");
        }
    }

    private static unsafe HashSet<int> QueryMembers(int processGroupId)
    {
        if (processGroupId <= 0)
        {
            throw new InvalidOperationException("The macOS process-group identity is invalid.");
        }

        Marshal.SetLastPInvokeError(0);
        var suggestedCapacity = MacOSNative.ListProcessGroupPids(processGroupId, null, 0);
        var initialError = Marshal.GetLastPInvokeError();
        if (suggestedCapacity == 0 && initialError != 0)
        {
            throw new Win32Exception(initialError, "The macOS membership authority is unavailable.");
        }
        if (suggestedCapacity < 32)
        {
            suggestedCapacity = 32;
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var capacity = checked(suggestedCapacity << attempt);
            var processIds = new int[capacity];
            int count;
            fixed (int* buffer = processIds)
            {
                Marshal.SetLastPInvokeError(0);
                count = MacOSNative.ListProcessGroupPids(
                    processGroupId,
                    buffer,
                    checked(capacity * sizeof(int)));
            }

            var queryError = Marshal.GetLastPInvokeError();
            if (count == 0 && queryError != 0)
            {
                throw new Win32Exception(queryError, "The macOS membership query failed.");
            }
            if (count < 0)
            {
                throw new InvalidOperationException("The macOS membership query returned an invalid count.");
            }
            if (count >= capacity)
            {
                continue;
            }

            var members = new HashSet<int>();
            for (var index = 0; index < count; index++)
            {
                var processId = processIds[index];
                if (processId <= 0 || !members.Add(processId))
                {
                    throw new InvalidOperationException(
                        "The macOS membership query returned ambiguous process identities.");
                }
            }

            return members;
        }

        throw new InvalidOperationException(
            "The macOS membership query did not converge within its bounded buffer growth.");
    }

    private static unsafe partial class MacOSNative
    {
        [LibraryImport(
            "/usr/lib/libproc.dylib",
            EntryPoint = "proc_listpgrppids",
            SetLastError = true)]
        public static partial int ListProcessGroupPids(
            int processGroupId,
            int* processIds,
            int bufferSize);
    }
}
