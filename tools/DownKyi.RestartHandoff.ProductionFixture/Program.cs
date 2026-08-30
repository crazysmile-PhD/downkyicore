using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using DownKyi.ProcessSupervision;

namespace DownKyi.RestartHandoff.ProductionFixture;

#pragma warning disable CA1515 // Platform tests locate the production fixture through its public marker.
public sealed class FixtureMarker;
#pragma warning restore CA1515

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The executable fixture converts every unexpected failure into typed cross-process evidence.")]
    public static async Task<int> Main(string[] arguments)
    {
        try
        {
            return arguments.FirstOrDefault() switch
            {
                "parent" => await RunParentAsync(arguments).ConfigureAwait(false),
                "helper" => await RunHelperAsync(arguments).ConfigureAwait(false),
                "replacement" => RunReplacement(arguments),
                "instant-exit" => 0,
                "hold" => await HoldAsync().ConfigureAwait(false),
                _ => 64
            };
        }
        catch (RestartHandoffException failure)
        {
            Emit(new ProductionRestartEvidence(
                "RestartFailure",
                failure.Failure.State,
                failure.Failure.Kind,
                failure.Failure.ParentIdentityAuthority,
                failure.Failure.HelperProcessId,
                0,
                failure.Failure.Detail));
            return 0;
        }
        catch (Exception failure)
        {
            Emit(new ProductionRestartEvidence(
                "FixtureFailure",
                RestartHandoffState.Failed,
                RestartHandoffFailureKind.HelperCrashed,
                null,
                null,
                0,
                $"{failure.GetType().Name}: {failure.Message}"));
            return 70;
        }
    }

    private static async Task<int> HoldAsync()
    {
        await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RunParentAsync(string[] arguments)
    {
        if (arguments.Length != 3 ||
            !int.TryParse(arguments[2], NumberStyles.None, CultureInfo.InvariantCulture,
                out var windowMilliseconds) ||
            windowMilliseconds <= 0)
        {
            return 64;
        }

        var scenario = arguments[1];
        var parentProcessId = Environment.ProcessId;
        if (scenario == "stale-identity")
        {
            using var stale = StartFixture("instant-exit");
            parentProcessId = stale.Id;
            await stale.WaitForExitAsync().ConfigureAwait(false);
        }

        var budget = TransitionBudget.Start(
            TimeSpan.FromMilliseconds(windowMilliseconds),
            TimeSpan.FromSeconds(1));
        var helperStartInfo = CreateFixtureStartInfo("helper", scenario);
        var lease = await RestartHandoffLease.PrepareAsync(
                helperStartInfo,
                parentProcessId,
                budget)
            .ConfigureAwait(false);
        Emit(new ProductionRestartEvidence(
            "Prepared",
            lease.State,
            null,
            lease.ParentIdentityAuthority,
            lease.HelperProcessId,
            0,
            null));

        if (scenario == "parent-exit-before-commit")
        {
            GC.KeepAlive(lease);
            return 0;
        }

        await using (lease.ConfigureAwait(false))
        {
            var command = await Console.In.ReadLineAsync().ConfigureAwait(false);
            if (command == "REVOKE")
            {
                await lease.RevokeAsync().ConfigureAwait(false);
                Emit(new ProductionRestartEvidence(
                    "Revoked",
                    lease.State,
                    null,
                    lease.ParentIdentityAuthority,
                    lease.HelperProcessId,
                    0,
                    null));
                return 0;
            }

            if (command?.StartsWith("CONSUME:", StringComparison.Ordinal) == true &&
                int.TryParse(command.AsSpan("CONSUME:".Length), out var delayMilliseconds) &&
                delayMilliseconds > 0)
            {
                await Task.Delay(delayMilliseconds).ConfigureAwait(false);
                command = await Console.In.ReadLineAsync().ConfigureAwait(false);
            }

            if (command is not "COMMIT" and not "COMMIT_HOLD" and not "DUPLICATE")
            {
                return 65;
            }

            lease.Commit();
            Emit(new ProductionRestartEvidence(
                "Committed",
                lease.State,
                null,
                lease.ParentIdentityAuthority,
                lease.HelperProcessId,
                0,
                null));
            if (command == "DUPLICATE")
            {
                try
                {
                    lease.Commit();
                }
                catch (InvalidOperationException failure)
                {
                    Emit(new ProductionRestartEvidence(
                        "DuplicateCommitRejected",
                        lease.State,
                        RestartHandoffFailureKind.AuthorizationRejected,
                        lease.ParentIdentityAuthority,
                        lease.HelperProcessId,
                        0,
                        failure.Message));
                }
            }

            if (command == "COMMIT_HOLD")
            {
                return string.Equals(
                    await Console.In.ReadLineAsync().ConfigureAwait(false),
                    "EXIT",
                    StringComparison.Ordinal)
                    ? 0
                    : 66;
            }

            return 0;
        }
    }

    private static async Task<int> RunHelperAsync(string[] arguments)
    {
        var parseResult = RestartHandoffProtocol.ParseRequest(arguments, out var request);
        if (parseResult != RestartHandoffRequestParseResult.Valid || request == null)
        {
            return 64;
        }

        var scenario = arguments.Length > 1 ? arguments[1] : "normal";
        if (scenario == "helper-crash-before-commit")
        {
            return 73;
        }

        if (scenario == "helper-crash-postcommit")
        {
            return await RunHelperCrashPostCommitAsync(request).ConfigureAwait(false);
        }

        var relaunchStartInfo = scenario.StartsWith(
            "relaunch-failure",
            StringComparison.Ordinal)
            ? new ProcessStartInfo
            {
                FileName = Path.Combine(
                    Path.GetTempPath(),
                    $"downkyi-missing-restart-{Guid.NewGuid():N}"),
                UseShellExecute = false
            }
            : CreateFixtureStartInfo("replacement", scenario);
        var outcome = scenario switch
        {
            "relaunch-failure-cleanup" =>
                await RestartHandoffHelper.ExecuteWithCleanupFailureForTestingAsync(
                        request,
                        relaunchStartInfo,
                        stage => new InvalidOperationException(
                            $"Injected {stage} cleanup failure."))
                    .ConfigureAwait(false),
            "cleanup-only" =>
                await RestartHandoffHelper.ExecuteWithCleanupFailureForTestingAsync(
                        request,
                        relaunchStartInfo,
                        stage => stage == RestartHandoffCleanupStage.AuthorizationEndpoint
                            ? new InvalidOperationException(
                                "Injected authorization cleanup failure.")
                            : null)
                    .ConfigureAwait(false),
            _ => await RestartHandoffHelper.ExecuteAsync(request, relaunchStartInfo)
                .ConfigureAwait(false)
        };
        Emit(new ProductionRestartEvidence(
            "HelperTerminal",
            outcome.State,
            outcome.Failure?.Kind,
            outcome.ParentIdentityAuthority,
            Environment.ProcessId,
            outcome.RelaunchAttempts,
            outcome.Failure?.Detail,
            outcome.Succeeded,
            outcome.CleanupFailures));
        return 0;
    }

    private static async Task<int> RunHelperCrashPostCommitAsync(
        RestartHandoffRequest request)
    {
        request.Deadline.ValidateCurrentClock();
        var status = new System.IO.Pipes.NamedPipeClientStream(
            ".",
            request.StatusEndpoint,
            System.IO.Pipes.PipeDirection.Out,
            System.IO.Pipes.PipeOptions.Asynchronous);
        await using var statusScope = status.ConfigureAwait(false);
        var authorization = new System.IO.Pipes.NamedPipeClientStream(
            ".",
            request.AuthorizationEndpoint,
            System.IO.Pipes.PipeDirection.In,
            System.IO.Pipes.PipeOptions.Asynchronous);
        await using var authorizationScope = authorization.ConfigureAwait(false);
        await status.ConnectAsync(CancellationToken.None)
            .WaitAsync(request.Deadline.RemainingOperation)
            .ConfigureAwait(false);
        await authorization.ConnectAsync(CancellationToken.None)
            .WaitAsync(request.Deadline.RemainingOperation)
            .ConfigureAwait(false);
        var parent = ParentLifetimeLeaseFactory.Create(request.ParentProcessId);
        await using var parentScope = parent.ConfigureAwait(false);
        if (parent.IsExited())
        {
            return 75;
        }

        var ready = JsonSerializer.Serialize(
            new RestartReadyStatus(
                Convert.ToHexString(request.Nonce),
                RestartHandoffState.Authorized,
                parent.IdentityAuthority,
                null,
                null),
            RestartJson.Options) + "\n";
        await status.WriteAsync(System.Text.Encoding.UTF8.GetBytes(ready))
            .AsTask()
            .WaitAsync(request.Deadline.RemainingOperation)
            .ConfigureAwait(false);
        await status.FlushAsync()
            .WaitAsync(request.Deadline.RemainingOperation)
            .ConfigureAwait(false);
        await status.DisposeAsync().ConfigureAwait(false);

        var rejection = await RestartAuthorizationFrame.ReadAsync(
                authorization,
                request.Deadline,
                request.Nonce,
                CancellationToken.None)
            .ConfigureAwait(false);
        if (rejection != null)
        {
            return 76;
        }

        Emit(new ProductionRestartEvidence(
            "HelperPostCommitCrash",
            RestartHandoffState.Committed,
            RestartHandoffFailureKind.HelperCrashed,
            parent.IdentityAuthority,
            Environment.ProcessId,
            0,
            "Injected post-commit helper crash before exact-parent wait."));
        return 74;
    }

    private static int RunReplacement(string[] arguments)
    {
        Emit(new ProductionRestartEvidence(
            "ReplacementStarted",
            RestartHandoffState.Completed,
            null,
            null,
            Environment.ProcessId,
            1,
            arguments.Length > 1 ? arguments[1] : null));
        return 0;
    }

    private static Process StartFixture(params string[] arguments)
    {
        return Process.Start(CreateFixtureStartInfo(arguments))
            ?? throw new InvalidOperationException(
                $"The '{arguments.FirstOrDefault()}' fixture did not start.");
    }

    private static ProcessStartInfo CreateFixtureStartInfo(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory
        };
        startInfo.ArgumentList.Add(typeof(FixtureMarker).Assembly.Location);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static void Emit(ProductionRestartEvidence evidence)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(evidence, JsonOptions));
        Console.Out.Flush();
    }
}

internal sealed record ProductionRestartEvidence(
    string Type,
    RestartHandoffState State,
    RestartHandoffFailureKind? FailureKind,
    ProcessIdentityAuthority? Authority,
    int? ProcessId,
    int RelaunchAttempts,
    string? Detail,
    bool? Succeeded = null,
    IReadOnlyList<RestartHandoffCleanupFailure>? CleanupFailures = null);
