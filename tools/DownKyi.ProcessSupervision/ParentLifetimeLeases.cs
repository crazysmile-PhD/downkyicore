using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

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
    private const uint WaitFailed = uint.MaxValue;
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

    protected override ValueTask<ParentLifetimeOutcome> WaitForExitCoreAsync(
        RestartHandoffDeadline deadline,
        Action? waitStartedForTesting,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deadline);
        cancellationToken.ThrowIfCancellationRequested();
        var timeout = checked((uint)deadline.RemainingOperationMillisecondsCeiling());
        uint result;
        if (!cancellationToken.CanBeCanceled)
        {
            waitStartedForTesting?.Invoke();
            result = WaitForSingleObject(_handle, timeout);
        }
        else
        {
            var cancellationHandle = cancellationToken.WaitHandle.SafeWaitHandle;
            var cancellationHandleReferenceAdded = false;
            try
            {
                cancellationHandle.DangerousAddRef(ref cancellationHandleReferenceAdded);
                waitStartedForTesting?.Invoke();
                result = WaitForMultipleObjects(
                    2,
                    [_handle, cancellationHandle.DangerousGetHandle()],
                    waitAll: false,
                    timeout);
            }
            finally
            {
                if (cancellationHandleReferenceAdded)
                {
                    cancellationHandle.DangerousRelease();
                }
            }
        }

        return result switch
        {
            WaitObject0 => ValueTask.FromResult(new ParentLifetimeOutcome(true)),
            WaitObject0 + 1 => ValueTask.FromException<ParentLifetimeOutcome>(
                CreateCancellationException(cancellationToken)),
            WaitTimeout => ValueTask.FromResult(new ParentLifetimeOutcome(false)),
            WaitFailed => ValueTask.FromException<ParentLifetimeOutcome>(
                new Win32Exception(
                    Marshal.GetLastPInvokeError(),
                    "WaitForMultipleObjects failed for the exact parent and cancellation signal.")),
            _ => ValueTask.FromException<ParentLifetimeOutcome>(new Win32Exception(
                Marshal.GetLastPInvokeError(),
                $"The exact-parent wait returned {result}."))
        };
    }

    private static OperationCanceledException CreateCancellationException(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new OperationCanceledException(
            "The cancellation wait handle signaled without token cancellation.",
            cancellationToken);
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
    private static extern uint WaitForMultipleObjects(
        uint count,
        [In] nint[] handles,
        [MarshalAs(UnmanagedType.Bool)] bool waitAll,
        uint milliseconds);

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
    private const int EventFdCloseOnExec = 0x00080000;
    private const int EventFdNonBlocking = 0x00000800;
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

    protected override async ValueTask<ParentLifetimeOutcome> WaitForExitCoreAsync(
        RestartHandoffDeadline deadline,
        Action? waitStartedForTesting,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deadline);
        cancellationToken.ThrowIfCancellationRequested();
        var timeout = deadline.RemainingOperationMillisecondsCeiling();
        if (!cancellationToken.CanBeCanceled)
        {
            waitStartedForTesting?.Invoke();
            return new ParentLifetimeOutcome(Poll(timeout));
        }

        SafeFileHandle? cancellationEvent = null;
        CancellationTokenRegistration registration = default;
        try
        {
            var cancellationFileDescriptor = eventfd(
                0,
                EventFdCloseOnExec | EventFdNonBlocking);
            if (cancellationFileDescriptor < 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastPInvokeError(),
                    "eventfd could not create the exact-parent cancellation wake signal.");
            }

            cancellationEvent = new SafeFileHandle(
                checked((nint)cancellationFileDescriptor),
                ownsHandle: true);
            registration = cancellationToken.UnsafeRegister(
                static state => ((LinuxCancellationWakeState)state!).Signal(),
                new LinuxCancellationWakeState(cancellationFileDescriptor));
            var descriptors = new[]
            {
                new PollDescriptor
                {
                    FileDescriptor = _pidfd,
                    Events = PollIn
                },
                new PollDescriptor
                {
                    FileDescriptor = cancellationFileDescriptor,
                    Events = PollIn
                }
            };
            waitStartedForTesting?.Invoke();
            var result = poll(descriptors, checked((nuint)descriptors.Length), timeout);
            if (result < 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastPInvokeError(),
                    "poll failed for the exact-parent pidfd and cancellation eventfd.");
            }

            var parentExited = ReadParentExit(descriptors[0]);
            if (parentExited)
            {
                return new ParentLifetimeOutcome(true);
            }

            ThrowIfInvalidCancellationEvents(descriptors[1].ReturnedEvents);
            if ((descriptors[1].ReturnedEvents & PollIn) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new InvalidOperationException(
                    "The cancellation eventfd signaled without token cancellation.");
            }

            if (result != 0)
            {
                throw new InvalidOperationException(
                    "poll returned without an exact-parent or cancellation event.");
            }

            return new ParentLifetimeOutcome(false);
        }
        finally
        {
            await registration.DisposeAsync().ConfigureAwait(false);
            cancellationEvent?.Dispose();
        }
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

        return ReadParentExit(descriptor);
    }

    private static bool ReadParentExit(PollDescriptor descriptor)
    {
        if (descriptor.ReturnedEvents == 0)
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

    private static void ThrowIfInvalidCancellationEvents(short events)
    {
        if ((events & (PollError | PollHangup | PollInvalid)) != 0)
        {
            throw new InvalidOperationException(
                $"Cancellation eventfd reported invalid events {events}.");
        }
    }

    private sealed class LinuxCancellationWakeState(int fileDescriptor)
    {
        private readonly int _fileDescriptor = fileDescriptor;

        public void Signal()
        {
            ulong value = 1;
            _ = write(_fileDescriptor, ref value, sizeof(ulong));
        }
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

    [DllImport("libc", EntryPoint = "poll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int poll(
        [In, Out] PollDescriptor[] descriptors,
        nuint count,
        int timeout);

    [DllImport("libc", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int eventfd(uint initialValue, int flags);

    [DllImport("libc", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern nint write(int fileDescriptor, ref ulong buffer, nuint count);

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
    private const short EventFilterUser = -10;
    private const ushort EventAdd = 0x0001;
    private const ushort EventEnable = 0x0004;
    private const ushort EventOneShot = 0x0010;
    private const ushort EventClear = 0x0020;
    private const ushort EventReceipt = 0x0040;
    private const ushort EventError = 0x4000;
    private const nuint CancellationEventIdentifier = 1;
    private const uint NoteTrigger = 0x01000000;
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
            try
            {
                RegisterCancellationEvent();
                return;
            }
            catch
            {
                CloseQueueAfterConstructionFailure();
                throw;
            }
        }

        var error = receipt[0].Data == 0
            ? Marshal.GetLastPInvokeError()
            : checked((int)receipt[0].Data);
        CloseQueueAfterConstructionFailure();
        throw new Win32Exception(
            error,
            "EVFILT_PROC NOTE_EXIT could not be armed before READY.");
    }

    public override ProcessIdentityAuthority IdentityAuthority =>
        ProcessIdentityAuthority.MacOSKqueueProcessNote;

    internal override bool IsExited()
    {
        var zero = new NativeTimespec();
        return Wait(ref zero, waitStartedForTesting: null, CancellationToken.None);
    }

    protected override ValueTask<ParentLifetimeOutcome> WaitForExitCoreAsync(
        RestartHandoffDeadline deadline,
        Action? waitStartedForTesting,
        CancellationToken cancellationToken)
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
        var exited = Wait(ref timeout, waitStartedForTesting, cancellationToken);
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

    private void RegisterCancellationEvent()
    {
        var change = new[]
        {
            new KernelEvent
            {
                Identifier = CancellationEventIdentifier,
                Filter = EventFilterUser,
                Flags = EventAdd | EventEnable | EventClear | EventReceipt
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
        throw new Win32Exception(
            error,
            "EVFILT_USER could not arm the exact-parent cancellation wake signal.");
    }

    private bool Wait(
        ref NativeTimespec timeout,
        Action? waitStartedForTesting,
        CancellationToken cancellationToken)
    {
        CancellationTokenRegistration registration = default;
        try
        {
            if (cancellationToken.CanBeCanceled)
            {
                registration = cancellationToken.UnsafeRegister(
                    static state => ((MacOsCancellationWakeState)state!).Signal(),
                    new MacOsCancellationWakeState(_queue));
            }

            waitStartedForTesting?.Invoke();
            var events = new KernelEvent[2];
            var result = kevent(_queue, null, 0, events, events.Length, ref timeout);
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

            for (var index = 0; index < result; index++)
            {
                ThrowIfError(events[index]);
                if (IsParentExit(events[index]))
                {
                    return true;
                }
            }

            for (var index = 0; index < result; index++)
            {
                if (IsCancellationWake(events[index]))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new InvalidOperationException(
                        "EVFILT_USER signaled without token cancellation.");
                }
            }

            throw new InvalidOperationException(
                "kqueue returned neither an exact-parent exit nor cancellation event.");
        }
        finally
        {
            registration.Dispose();
        }
    }

    private void CloseQueueAfterConstructionFailure()
    {
        var queue = Interlocked.Exchange(ref _queue, -1);
        if (queue >= 0)
        {
            _ = close(queue);
        }
    }

    private static bool IsParentExit(KernelEvent @event) =>
        @event.Filter == EventFilterProcess &&
        (@event.FilterFlags & NoteExit) != 0;

    private static bool IsCancellationWake(KernelEvent @event) =>
        @event.Identifier == CancellationEventIdentifier &&
        @event.Filter == EventFilterUser;

    private static void ThrowIfError(KernelEvent @event)
    {
        if ((@event.Flags & EventError) != 0)
        {
            throw new Win32Exception(
                checked((int)@event.Data),
                "kqueue watcher reported EV_ERROR.");
        }
    }

    private sealed class MacOsCancellationWakeState(int queue)
    {
        private readonly int _queue = queue;

        public void Signal()
        {
            var change = new[]
            {
                new KernelEvent
                {
                    Identifier = CancellationEventIdentifier,
                    Filter = EventFilterUser,
                    FilterFlags = NoteTrigger
                }
            };
            var zero = new NativeTimespec();
            _ = kevent(_queue, change, 1, null, 0, ref zero);
        }
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
