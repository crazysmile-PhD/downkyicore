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
        Justification = "Every cleanup phase must complete and retain its failures before resources are released.")]
    public static async Task CancelStopJoinValidateAndCleanupAsync<TResult>(
        Task<TResult> operation,
        Func<Task> requestCancellationAsync,
        TimeSpan boundedWait,
        Action<TResult> validateResult,
        params Action[] cleanupActions)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(requestCancellationAsync);
        ArgumentNullException.ThrowIfNull(validateResult);
        ArgumentNullException.ThrowIfNull(cleanupActions);
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

        try
        {
            await operation.WaitAsync(boundedWait).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AddFailure(failures, exception);
            fallbackRequired = true;
        }

        if (fallbackRequired)
        {
            CaptureFailures(cleanupActions, failures);
        }

        TResult? result = default;
        var resultAvailable = false;
        try
        {
            result = await operation.ConfigureAwait(false);
            resultAvailable = true;
        }
        catch (Exception exception)
        {
            AddFailure(failures, exception);
        }

        if (resultAvailable)
        {
            try
            {
                validateResult(result!);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        CaptureFailures(cleanupActions, failures);
        ThrowFailures("The started test operation and its cleanup both failed.", failures);
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
