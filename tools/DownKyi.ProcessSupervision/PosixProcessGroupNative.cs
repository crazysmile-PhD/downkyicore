using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DownKyi.ProcessSupervision;

internal static partial class PosixProcessGroupNative
{
    private const int PermissionDenied = 1;
    private const int NoSuchProcess = 3;
    private const int KillSignal = 9;

    public static void EstablishCurrentProcessGroup(int expectedProcessGroupId)
    {
        if (expectedProcessGroupId <= 0 || expectedProcessGroupId != Environment.ProcessId)
        {
            throw new ContainmentAuthorityException(
                ContainmentAuthorityFailureKind.MembershipAmbiguous,
                "The requested process-group identity does not match the inert anchor.");
        }

        if (SetProcessGroup(0, 0) != 0)
        {
            throw CreateOperationFailure(
                "The inert anchor could not establish its process group.");
        }

        if (GetCurrentProcessGroup() != expectedProcessGroupId)
        {
            throw new ContainmentAuthorityException(
                ContainmentAuthorityFailureKind.MembershipAmbiguous,
                "The inert anchor did not enter its expected process group.");
        }
    }

    public static void AssertProcessGroupMembership(int processId, int processGroupId)
    {
        if (processId <= 0 || processGroupId <= 0)
        {
            throw new ContainmentAuthorityException(
                ContainmentAuthorityFailureKind.MembershipAmbiguous,
                "The process-group membership identity is invalid.");
        }

        var actual = GetProcessGroup(processId);
        if (actual < 0)
        {
            throw CreateOperationFailure(
                "The process-group membership authority is unavailable.");
        }

        if (actual != processGroupId)
        {
            throw new ContainmentAuthorityException(
                ContainmentAuthorityFailureKind.MembershipAmbiguous,
                "The inert anchor is not in its expected process group.");
        }
    }

    public static ContainmentOccupancy ObserveAfterAnchorReap(int processGroupId)
    {
        var result = SignalProcessGroup(processGroupId, 0);
        var error = result == 0 ? 0 : Marshal.GetLastPInvokeError();
        if (result == 0)
        {
            return ContainmentOccupancy.Occupied;
        }

        if (error == NoSuchProcess)
        {
            return ContainmentOccupancy.Quiescent;
        }

        throw new ContainmentAuthorityException(
            ContainmentAuthorityFailureKind.MembershipAmbiguous,
            "The kernel process-group occupancy query was ambiguous.",
            new Win32Exception(error));
    }

    public static void Terminate(int processGroupId, bool allowDarwinPermissionDenied)
    {
        var result = SignalProcessGroup(processGroupId, KillSignal);
        var error = result == 0 ? 0 : Marshal.GetLastPInvokeError();
        ValidateTerminationRequestResult(result, error, allowDarwinPermissionDenied);
    }

    internal static void ValidateTerminationRequestResult(
        int result,
        int error,
        bool allowDarwinPermissionDenied)
    {
        if (result == 0 || error == NoSuchProcess)
        {
            return;
        }

        if (allowDarwinPermissionDenied && error == PermissionDenied)
        {
            return;
        }

        throw new ContainmentAuthorityException(
            ContainmentAuthorityFailureKind.OperationFailed,
            "The process-group termination request failed.",
            new Win32Exception(error));
    }

    public static int GetCurrentProcessGroupId()
    {
        return GetCurrentProcessGroup();
    }

    private static ContainmentAuthorityException CreateOperationFailure(string message)
    {
        return new ContainmentAuthorityException(
            ContainmentAuthorityFailureKind.OperationFailed,
            message,
            new Win32Exception(Marshal.GetLastPInvokeError()));
    }

    [LibraryImport("libc", EntryPoint = "setpgid", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int SetProcessGroup(int processId, int processGroupId);

    [LibraryImport("libc", EntryPoint = "getpgid", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int GetProcessGroup(int processId);

    [LibraryImport("libc", EntryPoint = "getpgrp", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int GetCurrentProcessGroup();

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int Kill(int processId, int signal);

    private static int SignalProcessGroup(int processGroupId, int signal)
    {
        if (processGroupId <= 0)
        {
            throw new ContainmentAuthorityException(
                ContainmentAuthorityFailureKind.MembershipAmbiguous,
                "The process-group identity is invalid.");
        }

        return Kill(-processGroupId, signal);
    }
}
