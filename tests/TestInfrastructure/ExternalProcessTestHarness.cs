using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace DownKyi.TestInfrastructure;

public sealed record ExternalProcessResult(
    int ExitCode,
    int ProcessId,
    string StandardOutput,
    string StandardError);

public sealed class ExternalProcessTimeoutException : TimeoutException
{
    public ExternalProcessTimeoutException()
        : this("The external test process timed out.")
    {
    }

    public ExternalProcessTimeoutException(string message)
        : base(message)
    {
    }

    public ExternalProcessTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal ExternalProcessTimeoutException(
        string fileName,
        int processId,
        TimeSpan timeout,
        string standardOutput,
        string standardError,
        IReadOnlyList<Exception> cleanupFailures)
        : base(
            $"External test process '{fileName}' (PID {processId}) exceeded {timeout}.",
            CreateCleanupException(cleanupFailures))
    {
        ProcessId = processId;
        StandardOutput = standardOutput;
        StandardError = standardError;
        CleanupFailures = cleanupFailures.ToArray();
    }

    public int ProcessId { get; }

    public string StandardOutput { get; } = string.Empty;

    public string StandardError { get; } = string.Empty;

    public IReadOnlyList<Exception> CleanupFailures { get; } = [];

    private static Exception? CreateCleanupException(IReadOnlyList<Exception> cleanupFailures)
    {
        return cleanupFailures.Count switch
        {
            0 => null,
            1 => cleanupFailures[0],
            _ => new AggregateException("External process cleanup failed.", cleanupFailures)
        };
    }
}

public static class ExternalProcessTestHarness
{
    public static Process Start(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException(
                    $"External test process '{startInfo.FileName}' did not start.");
            }

            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    public static ExternalProcessResult Run(
        ProcessStartInfo startInfo,
        TimeSpan executionTimeout,
        TimeSpan cleanupTimeout)
    {
        return Task.Run(
                () => RunAsync(
                    startInfo,
                    executionTimeout,
                    cleanupTimeout,
                    CancellationToken.None))
            .GetAwaiter()
            .GetResult();
    }

    public static async Task<ExternalProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        TimeSpan executionTimeout,
        TimeSpan cleanupTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ValidateTimeout(executionTimeout, nameof(executionTimeout));
        ValidateTimeout(cleanupTimeout, nameof(cleanupTimeout));

        using var process = Start(startInfo);
        var processId = process.Id;
        var standardOutput = startInfo.RedirectStandardOutput
            ? process.StandardOutput.ReadToEndAsync(CancellationToken.None)
            : Task.FromResult(string.Empty);
        var standardError = startInfo.RedirectStandardError
            ? process.StandardError.ReadToEndAsync(CancellationToken.None)
            : Task.FromResult(string.Empty);
        var exit = process.WaitForExitAsync(CancellationToken.None);

        try
        {
            await exit.WaitAsync(executionTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            var cleanupFailures = await StopCoreAsync(
                process,
                exit,
                standardOutput,
                standardError,
                cleanupTimeout).ConfigureAwait(false);
            var capturedOutput = await GetCompletedOutputAsync(standardOutput).ConfigureAwait(false);
            var capturedError = await GetCompletedOutputAsync(standardError).ConfigureAwait(false);
            throw new ExternalProcessTimeoutException(
                startInfo.FileName,
                processId,
                executionTimeout,
                capturedOutput,
                capturedError,
                cleanupFailures);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var cleanupFailures = await StopCoreAsync(
                process,
                exit,
                standardOutput,
                standardError,
                cleanupTimeout).ConfigureAwait(false);
            var cleanupException = CreateCleanupException(cleanupFailures);
            throw new OperationCanceledException(
                $"External test process '{startInfo.FileName}' was cancelled.",
                cleanupException,
                cancellationToken);
        }

        var drainFailures = await DrainAsync(
            standardOutput,
            standardError,
            cleanupTimeout).ConfigureAwait(false);
        ThrowCleanupFailures(drainFailures, "External process output drain failed.");

        return new ExternalProcessResult(
            process.ExitCode,
            processId,
            await standardOutput.ConfigureAwait(false),
            await standardError.ConfigureAwait(false));
    }

    public static async Task StopAsync(
        Process process,
        TimeSpan cleanupTimeout,
        params Task[] drainTasks)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(drainTasks);
        ValidateTimeout(cleanupTimeout, nameof(cleanupTimeout));

        var exit = process.WaitForExitAsync(CancellationToken.None);
        var failures = await StopCoreAsync(
            process,
            exit,
            drainTasks,
            cleanupTimeout).ConfigureAwait(false);
        ThrowCleanupFailures(failures, "External process cleanup failed.");
    }

    public static async Task RunWithCleanupAsync(
        Func<Task> body,
        params Func<Task>[] cleanupSteps)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(cleanupSteps);

        var bodyTask = InvokeAsync(body);
        await AwaitWithoutPropagatingAsync(bodyTask).ConfigureAwait(false);
        var primaryFailure = GetFailure(bodyTask);

        var cleanupFailures = new List<Exception>();
        foreach (var cleanupStep in cleanupSteps)
        {
            ArgumentNullException.ThrowIfNull(cleanupStep);
            var cleanupTask = InvokeAsync(cleanupStep);
            await AwaitWithoutPropagatingAsync(cleanupTask).ConfigureAwait(false);
            var cleanupFailure = GetFailure(cleanupTask);
            if (cleanupFailure is not null)
            {
                cleanupFailures.Add(cleanupFailure);
            }
        }

        if (primaryFailure is not null)
        {
            if (cleanupFailures.Count == 0)
            {
                ExceptionDispatchInfo.Capture(primaryFailure).Throw();
            }

            throw new AggregateException(
                "The test body failed and fixture cleanup also failed.",
                [primaryFailure, .. cleanupFailures]);
        }

        ThrowCleanupFailures(cleanupFailures, "Fixture cleanup failed.");
    }

    private static async Task<List<Exception>> StopCoreAsync(
        Process process,
        Task exitTask,
        Task standardOutput,
        Task standardError,
        TimeSpan cleanupTimeout)
    {
        return await StopCoreAsync(
            process,
            exitTask,
            [standardOutput, standardError],
            cleanupTimeout).ConfigureAwait(false);
    }

    private static async Task<List<Exception>> StopCoreAsync(
        Process process,
        Task exitTask,
        Task[] drainTasks,
        TimeSpan cleanupTimeout)
    {
        var failures = new List<Exception>();
        var cleanup = Stopwatch.StartNew();

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (IsProcessCleanupException(exception))
        {
            failures.Add(exception);
        }

        await ObserveWithinCleanupWindowAsync(
            exitTask,
            Remaining(cleanupTimeout, cleanup.Elapsed),
            "External process reap exceeded the cleanup timeout.",
            failures).ConfigureAwait(false);

        if (drainTasks.Length > 0)
        {
            await ObserveWithinCleanupWindowAsync(
                Task.WhenAll(drainTasks),
                Remaining(cleanupTimeout, cleanup.Elapsed),
                "External process output drain exceeded the cleanup timeout.",
                failures).ConfigureAwait(false);
        }

        return failures;
    }

    private static async Task<List<Exception>> DrainAsync(
        Task standardOutput,
        Task standardError,
        TimeSpan timeout)
    {
        var failures = new List<Exception>();
        await ObserveWithinCleanupWindowAsync(
            Task.WhenAll(standardOutput, standardError),
            timeout,
            "External process output drain exceeded the cleanup timeout.",
            failures).ConfigureAwait(false);
        return failures;
    }

    private static async Task ObserveWithinCleanupWindowAsync(
        Task task,
        TimeSpan remaining,
        string timeoutMessage,
        List<Exception> failures)
    {
        if (remaining <= TimeSpan.Zero)
        {
            failures.Add(new TimeoutException(timeoutMessage));
            return;
        }

        var timeout = Task.Delay(remaining);
        await Task.WhenAny(task, timeout).ConfigureAwait(false);
        if (!task.IsCompleted)
        {
            failures.Add(new TimeoutException(timeoutMessage));
            return;
        }

        var failure = GetFailure(task);
        if (failure is not null)
        {
            failures.Add(failure);
        }
    }

    private static bool IsProcessCleanupException(Exception exception)
    {
        return exception is InvalidOperationException
            or Win32Exception
            or NotSupportedException;
    }

    private static TimeSpan Remaining(TimeSpan timeout, TimeSpan elapsed)
    {
        var remaining = timeout - elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static async Task<string> GetCompletedOutputAsync(Task<string> output)
    {
        return output.Status == TaskStatus.RanToCompletion
            ? await output.ConfigureAwait(false)
            : string.Empty;
    }

    private static Exception? CreateCleanupException(List<Exception> cleanupFailures)
    {
        return cleanupFailures.Count switch
        {
            0 => null,
            1 => cleanupFailures[0],
            _ => new AggregateException("External process cleanup failed.", cleanupFailures)
        };
    }

    private static void ThrowCleanupFailures(
        List<Exception> cleanupFailures,
        string message)
    {
        if (cleanupFailures.Count == 0)
        {
            return;
        }

        if (cleanupFailures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(cleanupFailures[0]).Throw();
        }

        throw new AggregateException(message, cleanupFailures);
    }

    private static void ValidateTimeout(TimeSpan timeout, string parameterName)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                timeout,
                "Timeout must be greater than zero.");
        }
    }

    private static async Task InvokeAsync(Func<Task> action)
    {
        await action().ConfigureAwait(false);
    }

    private static async Task AwaitWithoutPropagatingAsync(Task task)
    {
        await Task.WhenAny(task).ConfigureAwait(false);
    }

    private static Exception? GetFailure(Task task)
    {
        if (task.IsCanceled)
        {
            return new TaskCanceledException(task);
        }

        if (!task.IsFaulted)
        {
            return null;
        }

        var aggregate = task.Exception;
        return aggregate.InnerExceptions.Count == 1
            ? aggregate.InnerExceptions[0]
            : aggregate;
    }
}
