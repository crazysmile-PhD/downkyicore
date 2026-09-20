using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;

namespace DownKyi.TestInfrastructure;

public static class FailurePreservingTestCleanup
{
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "This test-only boundary must retain any primary failure while cleanup runs.")]
    public static async Task RunAsync(Func<Task> operation, Func<Task> cleanup)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(cleanup);

        Exception? primaryFailure = null;
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        try
        {
            await cleanup().ConfigureAwait(false);
        }
        catch (Exception cleanupFailure)
        {
            if (primaryFailure is not null)
            {
                throw new AggregateException(
                    "The operation and its cleanup both failed.",
                    primaryFailure,
                    cleanupFailure);
            }

            throw;
        }

        if (primaryFailure is not null)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Every fallback failure must be retained without releasing a still-running test operation.")]
    public static async Task CancelStopAndJoinAsync(
        Task operation,
        Func<Task> requestCancellationAsync,
        TimeSpan boundedWait,
        params Action[] fallbackStops)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(requestCancellationAsync);
        ArgumentNullException.ThrowIfNull(fallbackStops);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(boundedWait, TimeSpan.Zero);

        var failures = new List<Exception>();
        var fallbackRequired = false;
        try
        {
            await requestCancellationAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
            fallbackRequired = true;
        }

        var operationObserved = false;
        try
        {
            await operation.WaitAsync(boundedWait).ConfigureAwait(false);
            operationObserved = true;
        }
        catch (Exception exception)
        {
            AddFailure(failures, exception);
            fallbackRequired = true;
        }

        if (fallbackRequired)
        {
            CaptureFailures(fallbackStops, failures);
        }

        if (!operationObserved)
        {
            try
            {
                await operation.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                AddFailure(failures, exception);
            }
        }

        ThrowFailures("The started test operation and its cleanup both failed.", failures);
    }

    public static void RunCleanupActions(params Action[] cleanupActions)
    {
        ArgumentNullException.ThrowIfNull(cleanupActions);

        var failures = new List<Exception>();
        CaptureFailures(cleanupActions, failures);
        ThrowFailures("One or more cleanup actions failed.", failures);
    }

    private static void AddFailure(List<Exception> failures, Exception failure)
    {
        if (!failures.Any(existing => ReferenceEquals(existing, failure)))
        {
            failures.Add(failure);
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Every cleanup action must be attempted and every failure retained.")]
    private static void CaptureFailures(Action[] cleanupActions, List<Exception> failures)
    {
        foreach (var cleanupAction in cleanupActions)
        {
            try
            {
                cleanupAction();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }
    }

    private static void ThrowFailures(string message, List<Exception> failures)
    {
        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        if (failures.Count > 1)
        {
            throw new AggregateException(message, failures);
        }
    }
}
