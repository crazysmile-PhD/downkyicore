using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace DownKyi.Architecture.Tests;

internal static class BoundedProcessRunner
{
    private static readonly TimeSpan ExecutionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TerminationTimeout = TimeSpan.FromSeconds(5);

    public static BoundedProcessResult Run(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            process.WaitForExitAsync(cancellationToken)
                .WaitAsync(ExecutionTimeout, cancellationToken)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception executionFailure)
        {
            var terminationFailure = Terminate(process);
            if (terminationFailure != null)
            {
                throw new AggregateException(
                    "The architecture-test child process failed and could not be terminated.",
                    executionFailure,
                    terminationFailure);
            }

            throw;
        }

        Task.WhenAll(standardOutput, standardError)
            .WaitAsync(TerminationTimeout, cancellationToken)
            .GetAwaiter()
            .GetResult();
        return new BoundedProcessResult(
            process.ExitCode,
            standardOutput.Result + standardError.Result);
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The bounded child-process owner must preserve any cleanup failure with the original process failure.")]
    private static Exception? Terminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(TerminationTimeout)
                    .GetAwaiter()
                    .GetResult();
            }

            return null;
        }
        catch (Exception failure)
        {
            return failure;
        }
    }
}

internal sealed record BoundedProcessResult(int ExitCode, string Output);
