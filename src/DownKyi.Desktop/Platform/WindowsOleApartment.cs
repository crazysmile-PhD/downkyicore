using System;
using System.Runtime.InteropServices;

namespace DownKyi.Platform;

internal sealed partial class WindowsOleApartment : IDisposable
{
    private const int Success = 0;
    private const int AlreadyInitialized = 1;
    private readonly bool _requiresUninitialize;

    private WindowsOleApartment(bool requiresUninitialize)
    {
        _requiresUninitialize = requiresUninitialize;
    }

    public static WindowsOleApartment Enter()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WindowsOleApartment(false);
        }

        var result = OleInitialize(IntPtr.Zero);
        if (result is not (Success or AlreadyInitialized))
        {
            Marshal.ThrowExceptionForHR(result);
        }

        return new WindowsOleApartment(true);
    }

    public void Dispose()
    {
        if (_requiresUninitialize)
        {
            OleUninitialize();
        }

        GC.SuppressFinalize(this);
    }

    [LibraryImport("ole32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int OleInitialize(IntPtr reserved);

    [LibraryImport("ole32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void OleUninitialize();
}
