using System.ComponentModel;
using System.Diagnostics;

namespace DownKyi.CentralTestRunner;

internal enum ProcessIdentityState
{
    Same,
    Gone,
    Unknown
}

internal static class ObservedProcessIdentity
{
    private static readonly TimeSpan ObservationInterval = TimeSpan.FromMilliseconds(10);

    internal static ProcessIdentityState Observe(
        ObservedProcess observed,
        Func<Process, DateTimeOffset>? readStartTimeUtc = null,
        Func<Process, bool>? readHasExited = null,
        Func<int, bool>? isProcessPresent = null,
        Action<Process>? onSame = null)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(observed.Pid);
        }
        catch (ArgumentException)
        {
            return ProcessIdentityState.Gone;
        }
        catch (Exception exception) when (IsObservationFailure(exception))
        {
            return ObservePresence(observed.Pid, isProcessPresent);
        }

        using (process)
        {
            ProcessIdentityState state;
            try
            {
                if (observed.StartTimeUtc is { } expectedStartTime)
                {
                    var currentStartTime = (readStartTimeUtc ?? ReadStartTimeUtc)(process);
                    if (currentStartTime != expectedStartTime)
                    {
                        return ProcessIdentityState.Gone;
                    }
                }

                if ((readHasExited ?? ReadHasExited)(process))
                {
                    return ProcessIdentityState.Gone;
                }

                state = observed.StartTimeUtc is null
                    ? ProcessIdentityState.Unknown
                    : ProcessIdentityState.Same;
            }
            catch (Exception exception) when (IsObservationFailure(exception))
            {
                try
                {
                    if ((readHasExited ?? ReadHasExited)(process))
                    {
                        return ProcessIdentityState.Gone;
                    }
                }
                catch (Exception exitException) when (IsObservationFailure(exitException))
                {
                    // Presence is the remaining independent observation.
                }

                state = ObservePresence(observed.Pid, isProcessPresent);
            }

            if (state == ProcessIdentityState.Same)
            {
                onSame?.Invoke(process);
            }

            return state;
        }
    }

    internal static async Task<ProcessIdentityState> ResolveUnknownAsync(
        ObservedProcess observed,
        CleanupDeadline deadline,
        Func<ObservedProcess, ProcessIdentityState>? observe = null)
    {
        var read = observe ?? (process => Observe(process));
        while (true)
        {
            var state = read(observed);
            if (state != ProcessIdentityState.Unknown || deadline.Remaining == TimeSpan.Zero)
            {
                return state;
            }

            await Task.Delay(Min(ObservationInterval, deadline.Remaining)).ConfigureAwait(false);
        }
    }

    internal static async Task WaitUntilGoneAsync(
        ObservedProcess observed,
        CancellationToken cancellationToken,
        Func<ObservedProcess, ProcessIdentityState>? observe = null)
    {
        var read = observe ?? (process => Observe(process));
        while (true)
        {
            var state = read(observed);
            if (state == ProcessIdentityState.Gone)
            {
                return;
            }

            if (state == ProcessIdentityState.Same && OperatingSystem.IsWindows())
            {
                try
                {
                    using var process = Process.GetProcessById(observed.Pid);
                    if (Observe(observed) == ProcessIdentityState.Same)
                    {
                        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                        return;
                    }
                }
                catch (ArgumentException)
                {
                    return;
                }
            }

            await Task.Delay(ObservationInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static ProcessIdentityState ObservePresence(int pid, Func<int, bool>? isProcessPresent)
    {
        try
        {
            return (isProcessPresent ?? IsProcessPresent)(pid)
                ? ProcessIdentityState.Unknown
                : ProcessIdentityState.Gone;
        }
        catch (Exception exception) when (IsObservationFailure(exception))
        {
            return ProcessIdentityState.Unknown;
        }
    }

    private static bool IsProcessPresent(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsObservationFailure(Exception exception)
    {
        return exception is InvalidOperationException or Win32Exception;
    }

    private static DateTimeOffset ReadStartTimeUtc(Process process) => process.StartTime.ToUniversalTime();

    private static bool ReadHasExited(Process process) => process.HasExited;

    private static TimeSpan Min(TimeSpan first, TimeSpan second) => first < second ? first : second;
}
