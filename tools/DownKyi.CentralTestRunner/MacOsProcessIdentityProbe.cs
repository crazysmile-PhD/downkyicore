using System.Runtime.InteropServices;

namespace DownKyi.CentralTestRunner;

internal enum MacOsProcessIdentityState
{
    Gone,
    SameIdentityZombie,
    SameIdentityLive,
    Reused,
    Unavailable,
}

internal readonly record struct MacOsProcessIdentityResult(
    MacOsProcessIdentityState State,
    int BytesReturned,
    int ProcessId,
    int ParentProcessId,
    uint Status,
    DateTimeOffset? StartTimeUtc,
    int Error);

// Thin native boundary: one libproc query supplies immutable identity/state data.
// Cancellation policy remains in BuildProcessRunner and is tested through an injected managed seam.
internal static class MacOsProcessIdentityProbe
{
    private const int ProcPidTaskBsdInfo = 3;
    private const ulong FindZombie = 1;
    private const uint ZombieStatus = 5;
    private const int NoSuchProcess = 3;

    internal static int NativeBufferSize => Marshal.SizeOf<ProcBsdInfo>();

    internal static MacOsProcessIdentityResult Probe(
        int processId,
        DateTimeOffset expectedStartTimeUtc)
    {
        var size = NativeBufferSize;
        var returned = NativeMethods.ProcPidInfo(
            processId,
            ProcPidTaskBsdInfo,
            FindZombie,
            out var info,
            size);
        var error = Marshal.GetLastPInvokeError();
        return Classify(
            processId,
            expectedStartTimeUtc,
            returned,
            error,
            info.ProcessId,
            info.ParentProcessId,
            info.Status,
            info.StartTimeSeconds,
            info.StartTimeMicroseconds);
    }

    internal static MacOsProcessIdentityResult Classify(
        int processId,
        DateTimeOffset expectedStartTimeUtc,
        int returned,
        int error,
        uint nativeProcessId,
        uint parentProcessId,
        uint status,
        ulong startTimeSeconds,
        ulong startTimeMicroseconds)
    {
        if (returned != NativeBufferSize)
        {
            return new MacOsProcessIdentityResult(
                returned == 0 && error == NoSuchProcess
                    ? MacOsProcessIdentityState.Gone
                    : MacOsProcessIdentityState.Unavailable,
                returned,
                processId,
                0,
                0,
                null,
                error);
        }

        var startTimeUtc = DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(
            startTimeSeconds + startTimeMicroseconds / 1_000_000d);
        var state = nativeProcessId != processId || startTimeUtc != expectedStartTimeUtc
            ? MacOsProcessIdentityState.Reused
            : status == ZombieStatus
                ? MacOsProcessIdentityState.SameIdentityZombie
                : MacOsProcessIdentityState.SameIdentityLive;
        return new MacOsProcessIdentityResult(
            state,
            returned,
            unchecked((int)nativeProcessId),
            unchecked((int)parentProcessId),
            status,
            startTimeUtc,
            error);
    }

    [StructLayout(LayoutKind.Explicit, Size = 136)]
    internal struct ProcBsdInfo
    {
        [FieldOffset(4)]
        internal uint Status;

        [FieldOffset(12)]
        internal uint ProcessId;

        [FieldOffset(16)]
        internal uint ParentProcessId;

        [FieldOffset(120)]
        internal ulong StartTimeSeconds;

        [FieldOffset(128)]
        internal ulong StartTimeMicroseconds;
    }

    private static class NativeMethods
    {
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        [DllImport("/usr/lib/libproc.dylib", EntryPoint = "proc_pidinfo", SetLastError = true)]
        internal static extern int ProcPidInfo(
            int processId,
            int flavor,
            ulong argument,
            out ProcBsdInfo buffer,
            int bufferSize);
    }
}
