using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace DownKyi.CentralTestRunner;

[SupportedOSPlatform("windows")]
internal static class WindowsProcessRelationshipSnapshot
{
    private const uint SnapshotProcesses = 0x00000002;
    private const int NoMoreFiles = 18;

    internal static Dictionary<int, int> ReadParentIds()
    {
        using var snapshot = NativeMethods.CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var result = new Dictionary<int, int>();
        var entry = new ProcessEntry32
        {
            Size = (uint)Marshal.SizeOf<ProcessEntry32>(),
            ExecutableFile = string.Empty
        };
        if (!NativeMethods.Process32First(snapshot, ref entry))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == NoMoreFiles)
            {
                return result;
            }

            throw new Win32Exception(error);
        }

        do
        {
            if (entry.ProcessId > 0 && entry.ProcessId <= int.MaxValue &&
                entry.ParentProcessId <= int.MaxValue)
            {
                result[(int)entry.ProcessId] = (int)entry.ParentProcessId;
            }

            entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
        }
        while (NativeMethods.Process32Next(snapshot, ref entry));

        var finalError = Marshal.GetLastPInvokeError();
        if (finalError != NoMoreFiles)
        {
            throw new Win32Exception(finalError);
        }

        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport(
            "kernel32.dll",
            EntryPoint = "Process32FirstW",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Process32First(
            SafeFileHandle snapshot,
            ref ProcessEntry32 entry);

        [DllImport(
            "kernel32.dll",
            EntryPoint = "Process32NextW",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Process32Next(
            SafeFileHandle snapshot,
            ref ProcessEntry32 entry);
    }
}
