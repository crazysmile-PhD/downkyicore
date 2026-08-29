using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace DownKyi.ProcessSupervision;

internal static class ParentLifetimeLeaseFactory
{
    public static ParentLifetimeLease Create(int processId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        return OperatingSystem.IsWindows()
            ? new WindowsParentLifetimeLease(processId)
            : OperatingSystem.IsLinux()
                ? new LinuxParentLifetimeLease(processId)
                : OperatingSystem.IsMacOS()
                    ? new MacOsParentLifetimeLease(processId)
                    : throw new PlatformNotSupportedException(
                        "Restart exact-parent watching is unsupported on this operating system.");
    }
}

[SuppressMessage(
    "Usage",
    "CA2216:Disposable types should declare finalizer",
    Justification = "The restart helper deterministically disposes its bounded native process handle.")]
internal sealed class WindowsParentLifetimeLease : ParentLifetimeLease
{
    private const uint Synchronize = 0x00100000;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;
    private nint _handle;

    public WindowsParentLifetimeLease(int processId)
    {
        _handle = OpenProcess(Synchronize, false, processId);
        if (_handle == 0)
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "OpenProcess could not retain the exact parent process object.");
        }
    }

    public override ProcessIdentityAuthority IdentityAuthority =>
        ProcessIdentityAuthority.WindowsProcessHandle;

    internal override bool IsExited()
    {
        return WaitForSingleObject(_handle, 0) switch
        {
            WaitObject0 => true,
            WaitTimeout => false,
            var result => throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                $"WaitForSingleObject returned {result}.")
        };
    }

    public override ValueTask<ParentLifetimeOutcome> WaitForExitAsync(
        RestartHandoffDeadline deadline,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deadline);
        cancellationToken.ThrowIfCancellationRequested();
        var result = WaitForSingleObject(
            _handle,
            checked((uint)deadline.RemainingOperationMillisecondsCeiling()));
        cancellationToken.ThrowIfCancellationRequested();
        return result switch
        {
            WaitObject0 => ValueTask.FromResult(new ParentLifetimeOutcome(true)),
            WaitTimeout => ValueTask.FromResult(new ParentLifetimeOutcome(false)),
            _ => ValueTask.FromException<ParentLifetimeOutcome>(new Win32Exception(
                Marshal.GetLastPInvokeError(),
                $"WaitForSingleObject returned {result}."))
        };
    }

    public override ValueTask DisposeAsync()
    {
        var handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0 && !CloseHandle(handle))
        {
            return ValueTask.FromException(new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "The exact-parent process handle could not be closed."));
        }

        return ValueTask.CompletedTask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}

[SuppressMessage(
    "Usage",
    "CA2216:Disposable types should declare finalizer",
    Justification = "The restart helper deterministically disposes its bounded pidfd.")]
internal sealed class LinuxParentLifetimeLease : ParentLifetimeLease
{
    private const nint PidfdOpenSystemCall = 434;
    private const short PollIn = 0x0001;
    private const short PollError = 0x0008;
    private const short PollHangup = 0x0010;
    private const short PollInvalid = 0x0020;
    private int _pidfd = -1;

    public LinuxParentLifetimeLease(int processId)
    {
        if (RuntimeInformation.ProcessArchitecture is not Architecture.X64 and
            not Architecture.Arm64)
        {
            throw new PlatformNotSupportedException(
                $"pidfd_open syscall mapping is unavailable for {RuntimeInformation.ProcessArchitecture}.");
        }

        var result = syscall(PidfdOpenSystemCall, processId, 0);
        if (result == -1)
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "pidfd_open could not retain the exact parent task.");
        }

        _pidfd = checked((int)result);
    }

    public override ProcessIdentityAuthority IdentityAuthority =>
        ProcessIdentityAuthority.LinuxPidFd;

    internal override bool IsExited()
    {
        return Poll(0);
    }

    public override ValueTask<ParentLifetimeOutcome> WaitForExitAsync(
        RestartHandoffDeadline deadline,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deadline);
        cancellationToken.ThrowIfCancellationRequested();
        var exited = Poll(deadline.RemainingOperationMillisecondsCeiling());
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new ParentLifetimeOutcome(exited));
    }

    public override ValueTask DisposeAsync()
    {
        var pidfd = Interlocked.Exchange(ref _pidfd, -1);
        if (pidfd >= 0 && close(pidfd) != 0)
        {
            return ValueTask.FromException(new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "The exact-parent pidfd could not be closed."));
        }

        return ValueTask.CompletedTask;
    }

    private bool Poll(int timeoutMilliseconds)
    {
        var descriptor = new PollDescriptor
        {
            FileDescriptor = _pidfd,
            Events = PollIn
        };
        var result = poll(ref descriptor, 1, timeoutMilliseconds);
        if (result < 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "poll failed for pidfd.");
        }

        if (result == 0)
        {
            return false;
        }

        if ((descriptor.ReturnedEvents & (PollError | PollInvalid)) != 0)
        {
            throw new InvalidOperationException(
                $"pidfd reported invalid events {descriptor.ReturnedEvents}.");
        }

        return (descriptor.ReturnedEvents & (PollIn | PollHangup)) != 0
            ? true
            : throw new InvalidOperationException(
                $"pidfd poll returned unexpected events {descriptor.ReturnedEvents}.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollDescriptor
    {
        public int FileDescriptor;
        public short Events;
        public short ReturnedEvents;
    }

    [DllImport("libc", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern nint syscall(nint number, int processId, uint flags);

    [DllImport("libc", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int poll(ref PollDescriptor descriptors, nuint count, int timeout);

    [DllImport("libc", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int close(int fileDescriptor);
}

[SuppressMessage(
    "Usage",
    "CA2216:Disposable types should declare finalizer",
    Justification = "The restart helper deterministically disposes its bounded kqueue.")]
internal sealed class MacOsParentLifetimeLease : ParentLifetimeLease
{
    private const short EventFilterProcess = -5;
    private const ushort EventAdd = 0x0001;
    private const ushort EventEnable = 0x0004;
    private const ushort EventOneShot = 0x0010;
    private const ushort EventReceipt = 0x0040;
    private const ushort EventError = 0x4000;
    private const uint NoteExit = 0x80000000;
    private int _queue = -1;

    public MacOsParentLifetimeLease(int processId)
    {
        _queue = kqueue();
        if (_queue < 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "kqueue creation failed.");
        }

        var change = new[]
        {
            new KernelEvent
            {
                Identifier = checked((nuint)processId),
                Filter = EventFilterProcess,
                Flags = EventAdd | EventEnable | EventOneShot | EventReceipt,
                FilterFlags = NoteExit
            }
        };
        var receipt = new KernelEvent[1];
        var zero = new NativeTimespec();
        var result = kevent(_queue, change, 1, receipt, 1, ref zero);
        if (result == 1 &&
            (receipt[0].Flags & EventError) != 0 &&
            receipt[0].Data == 0)
        {
            return;
        }

        var error = receipt[0].Data == 0
            ? Marshal.GetLastPInvokeError()
            : checked((int)receipt[0].Data);
        var queue = Interlocked.Exchange(ref _queue, -1);
        if (queue >= 0)
        {
            _ = close(queue);
        }
        throw new Win32Exception(
            error,
            "EVFILT_PROC NOTE_EXIT could not be armed before READY.");
    }

    public override ProcessIdentityAuthority IdentityAuthority =>
        ProcessIdentityAuthority.MacOSKqueueProcessNote;

    internal override bool IsExited()
    {
        var zero = new NativeTimespec();
        return Wait(ref zero);
    }

    public override ValueTask<ParentLifetimeOutcome> WaitForExitAsync(
        RestartHandoffDeadline deadline,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deadline);
        cancellationToken.ThrowIfCancellationRequested();
        var remaining = deadline.RemainingOperation;
        var timeout = new NativeTimespec
        {
            Seconds = (long)remaining.TotalSeconds,
            Nanoseconds = checked((nint)((remaining -
                TimeSpan.FromSeconds((long)remaining.TotalSeconds)).Ticks * 100L))
        };
        var exited = Wait(ref timeout);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new ParentLifetimeOutcome(exited));
    }

    public override ValueTask DisposeAsync()
    {
        var queue = Interlocked.Exchange(ref _queue, -1);
        if (queue >= 0 && close(queue) != 0)
        {
            return ValueTask.FromException(new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "The exact-parent kqueue could not be closed."));
        }

        return ValueTask.CompletedTask;
    }

    private bool Wait(ref NativeTimespec timeout)
    {
        var events = new KernelEvent[1];
        var result = kevent(_queue, null, 0, events, 1, ref timeout);
        if (result < 0)
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "kevent exact-parent wait failed.");
        }

        if (result == 0)
        {
            return false;
        }

        if ((events[0].Flags & EventError) != 0)
        {
            throw new Win32Exception(
                checked((int)events[0].Data),
                "kqueue process watcher reported EV_ERROR.");
        }

        return events[0].Filter == EventFilterProcess &&
            (events[0].FilterFlags & NoteExit) != 0
            ? true
            : throw new InvalidOperationException(
                "kqueue returned a non-exit process event.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KernelEvent
    {
        public nuint Identifier;
        public short Filter;
        public ushort Flags;
        public uint FilterFlags;
        public nint Data;
        public nint UserData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeTimespec
    {
        public long Seconds;
        public nint Nanoseconds;
    }

    [DllImport("libSystem.B.dylib", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int kqueue();

    [DllImport("libSystem.B.dylib", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int kevent(
        int queue,
        [In] KernelEvent[]? changes,
        int changeCount,
        [Out] KernelEvent[]? events,
        int eventCount,
        ref NativeTimespec timeout);

    [DllImport("libSystem.B.dylib", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int close(int fileDescriptor);
}
