using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;

namespace DownKyi.SystemBenchmarks;

internal sealed record SystemBenchmarkProcessCleanupOperations(
    Func<bool> HasExited,
    Action Kill,
    Func<CancellationToken, Task> WaitForExitAsync,
    Task StandardOutput,
    Task StandardError,
    Func<Task> CancelOutputAsync);

internal static class SystemBenchmarkProcessCleanup
{
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Every child cleanup failure is retained while all owned waits are bounded and joined.")]
    internal static async Task RunAsync(
        SystemBenchmarkProcessCleanupOperations operations,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        using var deadline = new CancellationTokenSource(timeout);
        var clock = Stopwatch.StartNew();
        var failures = new List<Exception>();
        if (!operations.HasExited())
        {
            try
            {
                operations.Kill();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        var exit = operations.WaitForExitAsync(deadline.Token);
        await CaptureFailureAsync(
            () => exit.WaitAsync(WorkWindow(timeout, clock), deadline.Token),
            "The benchmark child did not exit before the cleanup deadline.",
            failures).ConfigureAwait(false);

        var drain = Task.WhenAll(operations.StandardOutput, operations.StandardError);
        try
        {
            await drain.WaitAsync(WorkWindow(timeout, clock), deadline.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is TimeoutException or OperationCanceledException)
        {
            failures.Add(new TimeoutException(
                "The benchmark child output did not drain before the cleanup deadline.",
                exception));
            await CaptureFailureAsync(
                () => operations.CancelOutputAsync()
                    .WaitAsync(Remaining(timeout, clock), deadline.Token),
                "The benchmark child output cancellation failed.",
                failures).ConfigureAwait(false);
            await CaptureFailureAsync(
                () => ObserveCanceledDrainAsync(drain, Remaining(timeout, clock), deadline.Token),
                "The benchmark child output readers did not stop before the cleanup deadline.",
                failures).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (failures.Count == 0)
        {
            return;
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        throw new AggregateException("Benchmark child cleanup had multiple failures.", failures);
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Every cleanup failure is retained and reported after the remaining owners run.")]
    private static async Task CaptureFailureAsync(
        Func<Task> operation,
        string timeoutMessage,
        List<Exception> failures)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            failures.Add(new TimeoutException(timeoutMessage, exception));
        }
        catch (OperationCanceledException exception)
        {
            failures.Add(new TimeoutException(timeoutMessage, exception));
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private static async Task ObserveCanceledDrainAsync(
        Task drain,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            await drain.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (drain.IsCanceled)
        {
            // Cancellation is the requested bounded termination of the output readers.
        }
    }

    private static TimeSpan Remaining(TimeSpan timeout, Stopwatch clock) =>
        timeout > clock.Elapsed ? timeout - clock.Elapsed : TimeSpan.Zero;

    private static TimeSpan WorkWindow(TimeSpan timeout, Stopwatch clock)
    {
        var remaining = Remaining(timeout, clock);
        var cleanupReserve = TimeSpan.FromTicks(Math.Min(
            TimeSpan.FromSeconds(1).Ticks,
            remaining.Ticks / 4));
        return remaining - cleanupReserve;
    }
}
