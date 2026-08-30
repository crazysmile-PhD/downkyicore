using System.Diagnostics;
using DownKyi.ProcessSupervision;

namespace DownKyi.Architecture.Tests;

internal static class BoundedProcessRunner
{
    private static readonly TimeSpan ExecutionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TerminationTimeout = TimeSpan.FromSeconds(5);

    public static BoundedProcessResult Run(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken,
        TimeSpan? executionTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (startInfo.UseShellExecute)
        {
            throw new InvalidOperationException(
                "The bounded architecture child cannot use shell execution.");
        }
        if (!string.IsNullOrWhiteSpace(startInfo.Arguments))
        {
            throw new InvalidOperationException(
                "The bounded architecture child requires an immutable ArgumentList.");
        }

        var workingDirectory = string.IsNullOrWhiteSpace(startInfo.WorkingDirectory)
            ? Environment.CurrentDirectory
            : startInfo.WorkingDirectory;
        var environment = startInfo.Environment.ToDictionary(
            entry => entry.Key,
            entry => (string?)entry.Value,
            StringComparer.Ordinal);
        var budget = TransitionBudget.Start(
            executionTimeout ?? ExecutionTimeout,
            TerminationTimeout);
        var lease = OwnedProcessLease.StartAsync(
                new LaunchSpec(
                    startInfo.FileName,
                    startInfo.ArgumentList,
                    workingDirectory,
                    environment,
                    closeStandardInput: true),
                budget,
                cancellationToken)
            .GetAwaiter()
            .GetResult();
        try
        {
            var outcome = lease.WaitAsync(cancellationToken)
                .GetAwaiter()
                .GetResult();
            return new BoundedProcessResult(
                outcome.ExitCode,
                outcome.StandardOutput + outcome.StandardError);
        }
        finally
        {
            lease.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}

internal sealed record BoundedProcessResult(int ExitCode, string Output);
