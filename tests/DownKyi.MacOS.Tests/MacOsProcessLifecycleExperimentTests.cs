using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using DownKyi.CentralTestRunner;

namespace DownKyi.MacOS.Tests;

[SupportedOSPlatform("macos")]
public sealed class MacOsProcessLifecycleExperimentTests
{
    private const int TrialLimit = 50;
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task SingleChildTerminationObservationExperiment()
    {
        var counts = Enum.GetValues<ObservedProcessOutcomeCategory>()
            .ToDictionary(category => category, _ => 0);
        LifecycleExperimentHit? firstAmbiguous = null;
        var executedTrials = 0;
        for (var trial = 0; trial < TrialLimit; trial++)
        {
            var result = await RunTrialAsync().ConfigureAwait(true);
            counts[result.Outcome.Category]++;
            executedTrials++;
            if (result.Outcome.Category == ObservedProcessOutcomeCategory.IdentityFailureAmbiguous)
            {
                firstAmbiguous = result;
                break;
            }
        }

        var summary = $"macos-single-child-lifecycle trials={executedTrials} " +
            $"terminal-before-open={counts[ObservedProcessOutcomeCategory.TerminalBeforeOpen]} " +
            $"identity-success-then-reap={counts[ObservedProcessOutcomeCategory.IdentitySuccessThenReap]} " +
            $"identity-failure-terminal-confirmed={counts[ObservedProcessOutcomeCategory.IdentityFailureTerminalConfirmed]} " +
            $"identity-failure-ambiguous={counts[ObservedProcessOutcomeCategory.IdentityFailureAmbiguous]} " +
            $"live-process={counts[ObservedProcessOutcomeCategory.LiveProcess]} " +
            $"other={counts[ObservedProcessOutcomeCategory.Other]}";
        if (firstAmbiguous is { } hit)
        {
            summary += $" firstAmbiguous=[snapshotStart={hit.SnapshotStartTime:O} " +
                $"{hit.Outcome.Markers.FormatFailure(hit.Outcome.Exception!, cancellationRequested: false)} " +
                $"cleanup={hit.CleanupResult}]";
        }

        Console.WriteLine(summary);
    }

    private static async Task<LifecycleExperimentHit> RunTrialAsync()
    {
        Process? fixture = null;
        ObservedProcessObservationOutcome? outcome = null;
        DateTimeOffset snapshotStartTime = default;
        var targetPid = 0;
        await RunWithPreservedCleanupAsync(
            async () =>
            {
                fixture = await StartHoldingFixtureAsync().ConfigureAwait(true);
                targetPid = fixture.Id;
                snapshotStartTime = fixture.StartTime.ToUniversalTime();
                BuildProcessRunner.KillOwnedProcessTree(fixture);
                try
                {
                    await BuildProcessRunner.WaitForObservedProcessExitAsync(
                            new ObservedProcess
                            {
                                Pid = targetPid,
                                ParentPid = Environment.ProcessId,
                                StartTimeUtc = snapshotStartTime,
                            },
                            observationCompleted: value => outcome = value)
                        .WaitAsync(TestTimeout)
                        .ConfigureAwait(true);
                }
                catch (Exception exception) when (
                    outcome is { Category: ObservedProcessOutcomeCategory.IdentityFailureAmbiguous } value &&
                    ReferenceEquals(exception, value.Exception))
                {
                    // The experiment records this production-equivalent ambiguous observation.
                }
            },
            () => StopFixtureAsync(fixture)).ConfigureAwait(true);

        return new LifecycleExperimentHit(
            targetPid,
            snapshotStartTime,
            Assert.IsType<ObservedProcessObservationOutcome>(outcome),
            "completed");
    }

    private static async Task<Process> StartHoldingFixtureAsync()
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("--runtimeconfig");
        startInfo.ArgumentList.Add(
            Path.Combine(AppContext.BaseDirectory, "DownKyi.MacOS.Tests.runtimeconfig.json"));
        startInfo.ArgumentList.Add(typeof(BuildProcessRunner).Assembly.Location);
        startInfo.ArgumentList.Add("fixture-hold");

        var process = new Process { StartInfo = startInfo };
        process.Start();
        try
        {
            var readyLine = await process.StandardOutput.ReadLineAsync()
                .WaitAsync(TestTimeout, TestContext.Current.CancellationToken)
                .ConfigureAwait(false);
            Assert.StartsWith("fixture-ready pid=", readyLine, StringComparison.Ordinal);
            return process;
        }
        catch
        {
            await StopFixtureAsync(process).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task StopFixtureAsync(Process? process)
    {
        if (process is null)
        {
            return;
        }

        using (process)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync().WaitAsync(TestTimeout).ConfigureAwait(false);
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The experiment must preserve its primary observation failure while reaping each child.")]
    private static async Task RunWithPreservedCleanupAsync(Func<Task> operation, Func<Task> cleanup)
    {
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
                    "The lifecycle trial and its cleanup both failed.",
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

    private readonly record struct LifecycleExperimentHit(
        int TargetPid,
        DateTimeOffset SnapshotStartTime,
        ObservedProcessObservationOutcome Outcome,
        string CleanupResult);
}
