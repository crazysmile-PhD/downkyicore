using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;

namespace DownKyi.Tests;

internal sealed record Aria2TlsProcessCleanupOperations(
    Func<bool> HasExited,
    Func<CancellationToken, Task>? RequestShutdownAsync,
    Func<CancellationToken, Task> WaitForExitAsync,
    Action Kill,
    Task StandardOutput,
    Task StandardError,
    Func<Task> CancelOutputAsync);

internal static class Aria2TlsProcessCleanup
{
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The bounded owner must retain the first failure while all process cleanup stages run.")]
    internal static async Task RunAsync(
        Aria2TlsProcessCleanupOperations operations,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        using var deadline = new CleanupDeadline(timeout);
        using var shutdownCancellation = new CancellationTokenSource();
        Task? shutdown = null;
        var shutdownNeedsJoin = false;
        Task? exit = null;
        Exception? primaryFailure = null;
        var cleanupFailures = new List<Exception>();

        if (!operations.HasExited())
        {
            var forceKill = operations.RequestShutdownAsync is null;
            var killRequested = false;
            if (operations.RequestShutdownAsync is not null)
            {
                shutdown = RequestShutdownBestEffortAsync(
                    operations.RequestShutdownAsync,
                    shutdownCancellation.Token);
                try
                {
                    await shutdown.WaitAsync(deadline.WorkWindow, deadline.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is TimeoutException or OperationCanceledException)
                {
                    shutdownNeedsJoin = true;
                    primaryFailure = new TimeoutException(
                        "The aria2 force-shutdown request exceeded the cleanup deadline.",
                        exception);
                    await CaptureCleanupFailureAsync(
                        () => shutdownCancellation.CancelAsync()
                            .WaitAsync(deadline.Remaining, deadline.Token),
                        "The aria2 force-shutdown cancellation did not complete before the cleanup deadline.",
                        cleanupFailures).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    primaryFailure = exception;
                }
            }

            if (forceKill && !operations.HasExited())
            {
                try
                {
                    operations.Kill();
                    killRequested = true;
                }
                catch (Exception exception)
                {
                    cleanupFailures.Add(exception);
                }
            }

            exit = operations.WaitForExitAsync(deadline.Token);
            if (!forceKill && primaryFailure is null)
            {
                try
                {
                    await exit.WaitAsync(deadline.WorkWindow, deadline.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is TimeoutException or OperationCanceledException)
                {
                    // Graceful shutdown used its work window. Escalate to the
                    // bounded kill/reap path below; that path owns the outcome.
                    _ = exception;
                }
                catch (Exception exception)
                {
                    primaryFailure = exception;
                }
            }

            if (!killRequested && !operations.HasExited())
            {
                try
                {
                    operations.Kill();
                }
                catch (Exception exception)
                {
                    cleanupFailures.Add(exception);
                }
            }
        }

        exit ??= operations.WaitForExitAsync(deadline.Token);
        await CaptureCleanupFailureAsync(
            () => exit.WaitAsync(deadline.Remaining, deadline.Token),
            "The aria2 process could not be reaped before the cleanup deadline.",
            cleanupFailures).ConfigureAwait(false);

        var ownedTasks = !shutdownNeedsJoin
            ? new[] { operations.StandardOutput, operations.StandardError }
            : new[] { shutdown!, operations.StandardOutput, operations.StandardError };
        var drain = Task.WhenAll(ownedTasks);
        try
        {
            await drain.WaitAsync(deadline.WorkWindow, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is TimeoutException or OperationCanceledException)
        {
            primaryFailure ??= new TimeoutException(
                "The aria2 stdout/stderr drain exceeded the cleanup deadline.",
                exception);
            await CaptureCleanupFailureAsync(
                () => operations.CancelOutputAsync()
                    .WaitAsync(deadline.Remaining, deadline.Token),
                "The aria2 output cancellation did not complete before the cleanup deadline.",
                cleanupFailures).ConfigureAwait(false);
            await CaptureCleanupFailureAsync(
                () => ObserveCanceledDrainAsync(
                    drain,
                    deadline.Remaining,
                    deadline.Token),
                "The aria2 stdout/stderr readers did not stop before the cleanup deadline.",
                cleanupFailures).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (primaryFailure is null)
            {
                primaryFailure = exception;
            }
            else
            {
                cleanupFailures.Add(exception);
            }
        }

        if (primaryFailure is null && cleanupFailures.Count == 0)
        {
            return;
        }

        if (primaryFailure is not null && cleanupFailures.Count == 0)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }

        var failures = primaryFailure is null
            ? cleanupFailures
            : new[] { primaryFailure }.Concat(cleanupFailures).ToList();
        throw new AggregateException("aria2 process cleanup failed.", failures);
    }

    private static async Task RequestShutdownBestEffortAsync(
        Func<CancellationToken, Task> requestShutdownAsync,
        CancellationToken cancellationToken)
    {
        try
        {
            await requestShutdownAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // The process may already be exiting. The authoritative exit wait follows.
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The owner canceled the best-effort RPC before force-killing the process.
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

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Every cleanup failure is retained and reported after the remaining owners run.")]
    private static async Task CaptureCleanupFailureAsync(
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

    private sealed class CleanupDeadline : IDisposable
    {
        private readonly TimeSpan timeout;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly CancellationTokenSource cancellation;

        internal CleanupDeadline(TimeSpan timeout)
        {
            this.timeout = timeout;
            cancellation = new CancellationTokenSource(timeout);
        }

        internal CancellationToken Token => cancellation.Token;

        internal TimeSpan Remaining => timeout > clock.Elapsed
            ? timeout - clock.Elapsed
            : TimeSpan.Zero;

        internal TimeSpan WorkWindow
        {
            get
            {
                var remaining = Remaining;
                var cleanupReserve = TimeSpan.FromTicks(Math.Min(
                    TimeSpan.FromSeconds(1).Ticks,
                    remaining.Ticks / 4));
                return remaining - cleanupReserve;
            }
        }

        public void Dispose()
        {
            cancellation.Dispose();
        }
    }
}
