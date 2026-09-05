using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DownKyi.Architecture.Tests;

internal static class WindowsDirectoryHandleForensics
{
    [SupportedOSPlatform("windows")]
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A failure-only forensic scanner must never replace the original sharing violation.")]
    internal static string Capture(
        string directory,
        int markerProcessId,
        int testhostProcessId,
        DateTimeOffset cancellationRequestedUtc)
    {
        var captureStartedUtc = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var target = NormalizePath(Path.GetFullPath(directory));
            using var probe = File.OpenHandle(
                Environment.ProcessPath ?? typeof(WindowsDirectoryHandleForensics).Assembly.Location,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var snapshot = SystemHandleSnapshot.Capture();
            var fileType = snapshot.FindTypeIndex(
                Environment.ProcessId,
                unchecked((nuint)probe.DangerousGetHandle().ToInt64()));
            if (fileType is null)
            {
                return FormatHeader(
                    captureStartedUtc,
                    stopwatch.Elapsed,
                    target,
                    cancellationRequestedUtc,
                    "scannerFailure=unable-to-identify-file-object-type");
            }

            var result = snapshot.FindOwners(target, fileType.Value);
            var builder = new StringBuilder();
            builder.Append(FormatHeader(
                captureStartedUtc,
                stopwatch.Elapsed,
                target,
                cancellationRequestedUtc,
                $"matches={result.Owners.Count} " +
                $"fileHandlesScanned={result.FileHandlesScanned} " +
                $"processOpenFailures={result.ProcessOpenFailures} " +
                $"duplicateFailures={result.DuplicateFailures} " +
                $"pathResolutionFailures={result.PathResolutionFailures}"));
            foreach (var owner in result.Owners)
            {
                var role = ClassifyRole(owner, markerProcessId, testhostProcessId);
                var birth = owner.StartTimeUtc is null
                    ? "unknown"
                    : owner.StartTimeUtc > cancellationRequestedUtc
                        ? "late-born-after-cancellation"
                        : "present-before-cancellation";
                builder.AppendLine();
                builder.Append("owner ")
                    .Append("pid=").Append(owner.ProcessId)
                    .Append(" parentPid=").Append(owner.ParentProcessId)
                    .Append(" handle=0x").Append(owner.Handle.ToString("x", CultureInfo.InvariantCulture))
                    .Append(" role=").Append(role)
                    .Append(" birth=").Append(birth)
                    .Append(" startUtc=").Append(owner.StartTimeUtc?.ToString("O") ?? "unknown")
                    .Append(" process=").Append(owner.ProcessName)
                    .Append(" exited=").Append(owner.HasExited?.ToString() ?? "unknown")
                    .Append(" path=").Append(owner.Path)
                    .AppendLine();
                builder.Append("commandLine=").AppendLine(owner.CommandLine);
                builder.Append("ancestorChain=").AppendLine(ReadAncestorChain(owner));
                builder.Append("cancellationSnapshotMembership=not-instrumented");
            }

            return builder.ToString();
        }
        catch (Exception exception)
        {
            return FormatHeader(
                captureStartedUtc,
                stopwatch.Elapsed,
                directory,
                cancellationRequestedUtc,
                $"scannerFailure={exception.GetType().FullName}: {exception.Message}");
        }
    }

    private static string FormatHeader(
        DateTimeOffset capturedUtc,
        TimeSpan duration,
        string target,
        DateTimeOffset cancellationRequestedUtc,
        string details) =>
        $"capturedUtc={capturedUtc:O} durationMs={duration.TotalMilliseconds:F3} " +
        $"cancellationRequestedUtc={cancellationRequestedUtc:O} target={target} {details}";

    [SupportedOSPlatform("windows")]
    private static string ReadAncestorChain(HandleOwner owner)
    {
        const int maximumDepth = 8;
        var builder = new StringBuilder();
        var current = owner;
        for (var depth = 0; depth < maximumDepth && current.ProcessId > 0; depth++)
        {
            if (depth > 0)
            {
                builder.Append(" <- ");
            }

            builder.Append(current.ProcessId)
                .Append('[').Append(current.ProcessName).Append(']')
                .Append(" start=").Append(current.StartTimeUtc?.ToString("O") ?? "unknown")
                .Append(" cmd=").Append(current.CommandLine);
            if (current.ParentProcessId <= 0 || current.ParentProcessId == current.ProcessId)
            {
                break;
            }

            current = ReadProcess(current.ParentProcessId);
        }

        return builder.ToString();
    }

    private static string ClassifyRole(
        HandleOwner owner,
        int markerProcessId,
        int testhostProcessId)
    {
        if (owner.ProcessId == testhostProcessId)
        {
            return "testhost";
        }

        if (owner.ProcessId == markerProcessId ||
            owner.CommandLine.Contains("fixture-hold-marker", StringComparison.OrdinalIgnoreCase))
        {
            return "marker-descendant";
        }

        if (owner.CommandLine.Contains("MSBuild.dll", StringComparison.OrdinalIgnoreCase) ||
            owner.CommandLine.Contains("/nodemode:", StringComparison.OrdinalIgnoreCase))
        {
            return "msbuild-node";
        }

        if (owner.CommandLine.Contains("dotnet", StringComparison.OrdinalIgnoreCase) &&
            owner.CommandLine.Contains("build", StringComparison.OrdinalIgnoreCase))
        {
            return "root-process";
        }

        return "unknown-process";
    }

    private static string NormalizePath(string value)
    {
        const string prefix = @"\\?\";
        var path = value.StartsWith(prefix, StringComparison.Ordinal) ? value[prefix.Length..] : value;
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    [SupportedOSPlatform("windows")]
    private static HandleOwner ReadProcess(int processId)
    {
        using var processHandle = NativeMethods.OpenProcess(
            NativeMethods.ProcessQueryLimitedInformation,
            false,
            processId);
        var parentProcessId = processHandle.IsInvalid ? 0 : ReadParentProcessId(processHandle);
        var commandLine = processHandle.IsInvalid ? "<unavailable>" : ReadCommandLine(processHandle);
        DateTimeOffset? startTimeUtc = null;
        bool? hasExited = null;
        var processName = "<unavailable>";
        try
        {
            using var process = Process.GetProcessById(processId);
            startTimeUtc = process.StartTime.ToUniversalTime();
            processName = process.ProcessName;
            hasExited = process.HasExited;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
        }

        return new HandleOwner(
            processId,
            parentProcessId,
            0,
            startTimeUtc,
            processName,
            commandLine,
            hasExited,
            string.Empty);
    }

    [SupportedOSPlatform("windows")]
    private static int ReadParentProcessId(SafeProcessHandle process)
    {
        var size = Marshal.SizeOf<ProcessBasicInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var status = NativeMethods.NtQueryInformationProcess(process, 0, buffer, size, out _);
            if (status != 0)
            {
                return 0;
            }

            var information = Marshal.PtrToStructure<ProcessBasicInformation>(buffer);
            return information.ParentProcessId > 0 && information.ParentProcessId <= int.MaxValue
                ? (int)information.ParentProcessId
                : 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [SupportedOSPlatform("windows")]
    private static string ReadCommandLine(SafeProcessHandle process)
    {
        const int processCommandLineInformation = 60;
        _ = NativeMethods.NtQueryInformationProcess(
            process,
            processCommandLineInformation,
            IntPtr.Zero,
            0,
            out var required);
        if (required <= 0)
        {
            return "<unavailable>";
        }

        var buffer = Marshal.AllocHGlobal(required);
        try
        {
            var status = NativeMethods.NtQueryInformationProcess(
                process,
                processCommandLineInformation,
                buffer,
                required,
                out _);
            if (status != 0)
            {
                return $"<unavailable:0x{status:x8}>";
            }

            var value = Marshal.PtrToStructure<UnicodeString>(buffer);
            return value.Buffer == IntPtr.Zero || value.Length == 0
                ? string.Empty
                : Marshal.PtrToStringUni(value.Buffer, value.Length / sizeof(char)) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private sealed record HandleOwner(
        int ProcessId,
        int ParentProcessId,
        nuint Handle,
        DateTimeOffset? StartTimeUtc,
        string ProcessName,
        string CommandLine,
        bool? HasExited,
        string Path);

    private sealed record OwnerScanResult(
        IReadOnlyList<HandleOwner> Owners,
        int FileHandlesScanned,
        int ProcessOpenFailures,
        int DuplicateFailures,
        int PathResolutionFailures);

    [SupportedOSPlatform("windows")]
    private sealed class SystemHandleSnapshot : IDisposable
    {
        private const int ExtendedHandleInformation = 64;
        private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
        private const uint DuplicateSameAccess = 0x00000002;
        private const uint FileTypeDisk = 0x0001;
        private readonly IntPtr buffer;
        private readonly nuint count;
        private readonly int entrySize = Marshal.SizeOf<SystemHandleEntry>();

        private SystemHandleSnapshot(IntPtr buffer)
        {
            this.buffer = buffer;
            count = unchecked((nuint)Marshal.ReadIntPtr(buffer).ToInt64());
        }

        internal static SystemHandleSnapshot Capture()
        {
            var size = 1024 * 1024;
            while (true)
            {
                var buffer = Marshal.AllocHGlobal(size);
                var status = NativeMethods.NtQuerySystemInformation(
                    ExtendedHandleInformation,
                    buffer,
                    size,
                    out var required);
                if (status == 0)
                {
                    return new SystemHandleSnapshot(buffer);
                }

                Marshal.FreeHGlobal(buffer);
                if (status != StatusInfoLengthMismatch)
                {
                    throw new Win32Exception(status, $"NtQuerySystemInformation failed: 0x{status:x8}");
                }

                size = Math.Max(size * 2, required + 64 * 1024);
            }
        }

        internal ushort? FindTypeIndex(int processId, nuint handle)
        {
            foreach (var entry in Entries())
            {
                if (entry.ProcessId == unchecked((nuint)processId) && entry.HandleValue == handle)
                {
                    return entry.ObjectTypeIndex;
                }
            }

            return null;
        }

        internal OwnerScanResult FindOwners(string target, ushort fileType)
        {
            var matches = new List<HandleOwner>();
            var processHandles = new Dictionary<int, SafeProcessHandle?>();
            var fileHandlesScanned = 0;
            var processOpenFailures = 0;
            var duplicateFailures = 0;
            var pathResolutionFailures = 0;
            try
            {
                foreach (var entry in Entries())
                {
                    if (entry.ObjectTypeIndex != fileType || entry.ProcessId == 0 || entry.ProcessId > int.MaxValue)
                    {
                        continue;
                    }

                    fileHandlesScanned++;
                    var processId = (int)entry.ProcessId;
                    if (!processHandles.TryGetValue(processId, out var source))
                    {
                        source = NativeMethods.OpenProcess(
                            NativeMethods.ProcessDuplicateHandle | NativeMethods.ProcessQueryLimitedInformation,
                            false,
                            processId);
                        if (source.IsInvalid)
                        {
                            source.Dispose();
                            source = null;
                            processOpenFailures++;
                        }

                        processHandles.Add(processId, source);
                    }

                    if (source is null || !NativeMethods.DuplicateHandle(
                            source,
                            new IntPtr(unchecked((long)entry.HandleValue)),
                            NativeMethods.GetCurrentProcess(),
                            out var duplicate,
                            0,
                            false,
                            DuplicateSameAccess))
                    {
                        duplicateFailures++;
                        continue;
                    }

                    using (duplicate)
                    {
                        if (NativeMethods.GetFileType(duplicate) != FileTypeDisk)
                        {
                            continue;
                        }

                        var value = GetFinalPath(duplicate);
                        if (value is null)
                        {
                            pathResolutionFailures++;
                            continue;
                        }

                        var path = NormalizePath(value);
                        if (!string.Equals(path, target, StringComparison.OrdinalIgnoreCase) &&
                            !path.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var process = ReadProcess(processId);
                        matches.Add(process with
                        {
                            Handle = entry.HandleValue,
                            Path = path,
                        });
                    }
                }
            }
            finally
            {
                foreach (var process in processHandles.Values)
                {
                    process?.Dispose();
                }
            }

            return new OwnerScanResult(
                matches,
                fileHandlesScanned,
                processOpenFailures,
                duplicateFailures,
                pathResolutionFailures);
        }

        public void Dispose() => Marshal.FreeHGlobal(buffer);

        private IEnumerable<SystemHandleEntry> Entries()
        {
            var address = IntPtr.Add(buffer, IntPtr.Size * 2);
            for (nuint index = 0; index < count; index++)
            {
                yield return Marshal.PtrToStructure<SystemHandleEntry>(address);
                address = IntPtr.Add(address, entrySize);
            }
        }

        private static string? GetFinalPath(SafeFileHandle handle)
        {
            var builder = new StringBuilder(1024);
            var length = NativeMethods.GetFinalPathNameByHandle(handle, builder, builder.Capacity, 0);
            if (length == 0)
            {
                return null;
            }

            if (length >= builder.Capacity)
            {
                builder.EnsureCapacity((int)length + 1);
                length = NativeMethods.GetFinalPathNameByHandle(handle, builder, builder.Capacity, 0);
            }

            return length > 0 && length < builder.Capacity ? builder.ToString() : null;
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct SystemHandleEntry
        {
            public readonly IntPtr Object;
            public readonly nuint ProcessId;
            public readonly nuint HandleValue;
            public readonly uint GrantedAccess;
            public readonly ushort CreatorBackTraceIndex;
            public readonly ushort ObjectTypeIndex;
            public readonly uint HandleAttributes;
            public readonly uint Reserved;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct ProcessBasicInformation
    {
        public readonly IntPtr Reserved1;
        public readonly IntPtr PebBaseAddress;
        public readonly IntPtr Reserved2A;
        public readonly IntPtr Reserved2B;
        public readonly nuint ProcessId;
        public readonly nuint ParentProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct UnicodeString
    {
        public readonly ushort Length;
        public readonly ushort MaximumLength;
        public readonly IntPtr Buffer;
    }

    [SupportedOSPlatform("windows")]
    private static class NativeMethods
    {
        internal const uint ProcessDuplicateHandle = 0x0040;
        internal const uint ProcessQueryLimitedInformation = 0x1000;

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("ntdll.dll", ExactSpelling = true)]
        internal static extern int NtQuerySystemInformation(
            int informationClass,
            IntPtr information,
            int informationLength,
            out int returnLength);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("ntdll.dll", ExactSpelling = true)]
        internal static extern int NtQueryInformationProcess(
            SafeProcessHandle processHandle,
            int informationClass,
            IntPtr information,
            int informationLength,
            out int returnLength);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        internal static extern SafeProcessHandle OpenProcess(
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            int processId);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DuplicateHandle(
            SafeProcessHandle sourceProcess,
            IntPtr sourceHandle,
            IntPtr targetProcess,
            out SafeFileHandle targetHandle,
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            uint options);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", ExactSpelling = true)]
        internal static extern IntPtr GetCurrentProcess();

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", ExactSpelling = true)]
        internal static extern uint GetFileType(SafeFileHandle fileHandle);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [SuppressMessage(
            "Performance",
            "CA1838:Avoid StringBuilder parameters for P/Invokes",
            Justification = "This temporary failure-only diagnostic favors a bounded, simple path buffer.")]
        [DllImport(
            "kernel32.dll",
            EntryPoint = "GetFinalPathNameByHandleW",
            ExactSpelling = true,
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        internal static extern uint GetFinalPathNameByHandle(
            SafeFileHandle fileHandle,
            StringBuilder filePath,
            int filePathLength,
            uint flags);
    }
}
