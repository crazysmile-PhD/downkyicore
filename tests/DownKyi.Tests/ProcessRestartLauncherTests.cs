using System.Diagnostics;
using System.Globalization;
using DownKyi.Platform;
using DownKyi.ProcessSupervision;

namespace DownKyi.Tests;

public sealed class ProcessRestartLauncherTests
{
    [Fact]
    public void NonHelperArgumentsRemainNormalDesktopStartup()
    {
        var result = ProcessRestartLauncher.TryParseRestartRequest(
            ["--unrelated", "value"],
            out var request);

        Assert.Equal(RestartHandoffRequestParseResult.NotRequested, result);
        Assert.Null(request);
    }

    [Theory]
    [InlineData("--downkyi-restart-handoff-v1")]
    [InlineData("--downkyi-restart-handoff-v1", "--restart-parent-process-id", "0")]
    [InlineData("--downkyi-restart-handoff-v1", "--restart-parent-process-id", "42", "--restart-authorization-endpoint", "descriptive-pipe-name")]
    public void MalformedHelperArgumentsFailClosed(params string[] arguments)
    {
        var result = ProcessRestartLauncher.TryParseRestartRequest(arguments, out var request);

        Assert.Equal(RestartHandoffRequestParseResult.Invalid, result);
        Assert.Null(request);
    }

    [Fact]
    public void HelperArgumentsCarryExactWatcherInputsAndOneAbsoluteDeadline()
    {
        var now = Stopwatch.GetTimestamp();
        var operationExpiry = checked(now + Stopwatch.Frequency * 10);
        var cleanupExpiry = checked(operationExpiry + Stopwatch.Frequency * 5);
        var arguments = new[]
        {
            "--downkyi-restart-handoff-v1",
            "--restart-parent-process-id",
            "42",
            "--restart-authorization-endpoint",
            "dkyi-0123456789abcdfg",
            "--restart-status-endpoint",
            "dkyi-0123456789abcdfh",
            "--restart-deadline-domain",
            OperatingSystem.IsWindows() ? "windows-qpc-v1" : "unix-monotonic-v1",
            "--restart-operation-expires-at",
            operationExpiry.ToString(CultureInfo.InvariantCulture),
            "--restart-cleanup-expires-at",
            cleanupExpiry.ToString(CultureInfo.InvariantCulture),
            "--restart-clock-frequency",
            Stopwatch.Frequency.ToString(CultureInfo.InvariantCulture),
            "--restart-authorization-nonce",
            new string('A', 64)
        };

        var result = ProcessRestartLauncher.TryParseRestartRequest(arguments, out var request);

        Assert.Equal(RestartHandoffRequestParseResult.Valid, result);
        Assert.NotNull(request);
        Assert.Equal(42, request.ParentProcessId);
        Assert.Equal(operationExpiry, request.Deadline.OperationExpiresAt);
        Assert.Equal(cleanupExpiry, request.Deadline.CleanupExpiresAt);
        Assert.DoesNotContain(
            arguments,
            argument => argument.Contains("started-at", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RestartStartInfoUsesArgumentListWithoutShellParsingOrLegacyIdentity()
    {
        var startInfo = ProcessRestartLauncher.CreateStartInfo();

        Assert.False(startInfo.UseShellExecute);
        Assert.DoesNotContain(
            startInfo.ArgumentList,
            argument => argument.Contains("restart-after-pid", StringComparison.Ordinal));
        Assert.DoesNotContain(
            startInfo.ArgumentList,
            argument => argument.Contains("started-at", StringComparison.Ordinal));
        Assert.DoesNotContain("--downkyi-restart-handoff-v1", startInfo.Arguments,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedHelperModeCannotFallThroughToDesktopStartup()
    {
        var failure = await Assert.ThrowsAsync<RestartHandoffException>(() =>
            ProcessRestartLauncher.RunHelperIfRequestedAsync(
                ["--downkyi-restart-handoff-v1"],
                TestContext.Current.CancellationToken));

        Assert.Equal(
            RestartHandoffFailureKind.AuthorizationRejected,
            failure.Failure.Kind);
    }

    [Fact]
    public void HelperCleanupOnlyFailureCannotReportSuccessfulProductExecution()
    {
        var cleanup = new RestartHandoffCleanupFailure(
            RestartHandoffCleanupStage.ParentLifetime,
            typeof(IOException).FullName!,
            "fixture cleanup failed");
        var outcome = new RestartHandoffOutcome(
            RestartHandoffState.Completed,
            ProcessIdentityAuthority.WindowsProcessHandle,
            RelaunchAttempts: 1,
            Failure: null)
        {
            CleanupFailures = [cleanup]
        };

        var failure = Assert.Throws<RestartHandoffException>(
            () => ProcessRestartLauncher.ThrowIfHelperFailed(outcome));

        Assert.Equal(RestartHandoffFailureKind.CleanupFailed, failure.Failure.Kind);
        Assert.Equal(RestartHandoffState.Completed, failure.Failure.State);
        Assert.Equal(cleanup, Assert.Single(failure.CleanupStageFailures));
        Assert.Single(failure.CleanupFailures);
    }

    [Fact]
    public void HelperPrimaryFailureRetainsIndependentCleanupEvidence()
    {
        var primary = new RestartHandoffFailure(
            RestartHandoffFailureKind.RelaunchFailed,
            RestartHandoffState.RelaunchStarted,
            ProcessIdentityAuthority.WindowsProcessHandle,
            HelperProcessId: 42,
            "fixture relaunch failed");
        var cleanup = new RestartHandoffCleanupFailure(
            RestartHandoffCleanupStage.StatusEndpoint,
            typeof(IOException).FullName!,
            "fixture status cleanup failed");
        var outcome = new RestartHandoffOutcome(
            RestartHandoffState.Failed,
            ProcessIdentityAuthority.WindowsProcessHandle,
            RelaunchAttempts: 1,
            Failure: primary)
        {
            CleanupFailures = [cleanup]
        };

        var failure = Assert.Throws<RestartHandoffException>(
            () => ProcessRestartLauncher.ThrowIfHelperFailed(outcome));

        Assert.Equal(primary, failure.Failure);
        Assert.Equal(cleanup, Assert.Single(failure.CleanupStageFailures));
        Assert.Single(failure.CleanupFailures);
    }

    [Fact]
    public void ProductTimeoutPolicyIsUnchanged()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            ProcessRestartLauncher.RestartParentExitTimeout);
        Assert.Equal(
            TimeSpan.FromSeconds(5),
            ProcessRestartLauncher.RestartHelperTerminationTimeout);
    }
}
