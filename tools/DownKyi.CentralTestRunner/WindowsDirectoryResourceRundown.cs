using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace DownKyi.CentralTestRunner;

[SupportedOSPlatform("windows")]
internal static class WindowsDirectoryResourceRundown
{
    private const uint DeleteAccess = 0x00010000;
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint ShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    internal static async Task WaitForDeleteAccessAsync(
        string resourcePath,
        TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);

        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            var error = TryAcquireDeleteAccess(resourcePath);
            if (error is 0 or ErrorFileNotFound or ErrorPathNotFound)
            {
                return;
            }

            if (error is not (ErrorSharingViolation or ErrorLockViolation))
            {
                throw new Win32Exception(
                    error,
                    $"Unable to verify DELETE readiness for '{resourcePath}'.");
            }

            if (Stopwatch.GetElapsedTime(started) >= timeout)
            {
                throw new DirectoryResourceRundownTimeoutException(resourcePath, timeout, error);
            }

            // Windows has no notification for a path's sharing state. Yield and
            // re-check the actual DELETE-access condition until the deadline.
            await Task.Yield();
        }
    }

    private static int TryAcquireDeleteAccess(string resourcePath)
    {
        using var handle = NativeMethods.CreateFile(
            resourcePath,
            DeleteAccess,
            ShareRead | ShareWrite | ShareDelete,
            IntPtr.Zero,
            OpenExisting,
            BackupSemantics,
            IntPtr.Zero);
        return handle.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
    }

    private static class NativeMethods
    {
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(
            "kernel32.dll",
            EntryPoint = "CreateFileW",
            ExactSpelling = true,
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);
    }
}

[SuppressMessage(
    "Design",
    "CA1032:Implement standard exception constructors",
    Justification = "This internal cleanup exception always requires resource and Win32 context.")]
internal sealed class DirectoryResourceRundownTimeoutException : TimeoutException
{
    internal DirectoryResourceRundownTimeoutException(
        string resourcePath,
        TimeSpan timeout,
        int win32Error)
        : base(
            $"DELETE access for '{resourcePath}' remained blocked after the bounded " +
            $"cleanup window of {timeout.TotalMilliseconds:F0} ms (Win32 error {win32Error}).")
    {
        ResourcePath = resourcePath;
        Win32Error = win32Error;
    }

    internal string ResourcePath { get; }

    internal int Win32Error { get; }
}
