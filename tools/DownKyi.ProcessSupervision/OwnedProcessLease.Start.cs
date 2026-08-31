using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;

namespace DownKyi.ProcessSupervision;

#pragma warning disable CA1515 // Public process-supervision boundary is consumed by PowerShell and platform tests.
public sealed partial class OwnedProcessLease
{
    private const string HostMode = "--owned-process-host";

    public static async Task<OwnedProcessLease> StartAsync(
        LaunchSpec launchSpec,
        TransitionBudget budget,
        ProcessContainmentRequirement containmentRequirement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launchSpec);
        ArgumentNullException.ThrowIfNull(budget);
        if (!Enum.IsDefined(containmentRequirement))
        {
            throw new ArgumentOutOfRangeException(nameof(containmentRequirement));
        }

        PlatformContainmentFacts platformFacts;
        IProcessContainmentBackend backend;
        try
        {
            platformFacts = PlatformProcessContainmentRouter.CapturePlatformFacts();
            backend = PlatformProcessContainmentRouter.Select(
                platformFacts,
                containmentRequirement);
        }
        catch (Exception failure) when (
            failure is ContainmentAuthorityException or PlatformNotSupportedException)
        {
            throw CreatePrelaunchFailure(failure, budget);
        }

        var pipeNames = CreatePipeNames(Guid.NewGuid());
        var commandPipeName = pipeNames.CommandPipeName;
        var statusPipeName = pipeNames.StatusPipeName;
        var commands = new NamedPipeServerStream(
            commandPipeName,
            PipeDirection.Out,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var status = new NamedPipeServerStream(
            statusPipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        Process? supervisor = null;
        IProcessContainmentLease? containment = null;
        try
        {
            supervisor = StartSupervisor(commandPipeName, statusPipeName);
            var standardOutput = supervisor.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var standardError = supervisor.StandardError.ReadToEndAsync(CancellationToken.None);
            containment = backend.Prepare(
                supervisor,
                platformFacts);
            containment.AttachAnchor(supervisor);

            var lease = new OwnedProcessLease(
                budget,
                supervisor,
                containment,
                commands,
                status,
                standardOutput,
                standardError);
            supervisor = null;
            containment = null;
            await lease.InitializeAsync(launchSpec, cancellationToken).ConfigureAwait(false);
            return lease;
        }
        catch (Exception failure)
        {
            if (supervisor != null)
            {
                await CleanupUnattachedSupervisorAsync(
                        supervisor,
                        commands,
                        status,
                        budget)
                    .ConfigureAwait(false);
            }
            else
            {
                await commands.DisposeAsync().ConfigureAwait(false);
                await status.DisposeAsync().ConfigureAwait(false);
            }
            containment?.Dispose();

            if (failure is OwnedProcessExecutionException)
            {
                throw;
            }

            throw CreatePrelaunchFailure(failure, budget);
        }
    }

    private async Task InitializeAsync(
        LaunchSpec launchSpec,
        CancellationToken cancellationToken)
    {
        try
        {
            await _budget.AwaitOperationAsync(
                    Task.WhenAll(
                        _commands.WaitForConnectionAsync(cancellationToken),
                        _status.WaitForConnectionAsync(cancellationToken)),
                    cancellationToken)
                .ConfigureAwait(false);
            await WriteOperationFrameAsync(
                    new AttachOwnershipFrame(_containment.Attachment),
                    cancellationToken)
                .ConfigureAwait(false);
            var ready = await ReadOperationFrameAsync<OwnershipReadyFrame>(cancellationToken)
                .ConfigureAwait(false);
            _containment.AssertAnchorOwned(_supervisor);
            if (!_containment.Metadata.Equals(ready.Ownership))
            {
                throw new ContainmentAuthorityException(
                    ContainmentAuthorityFailureKind.MembershipAmbiguous,
                    "The supervisor readiness metadata did not match the owner authority.");
            }

            _ownership = ready.Ownership;
            _proof.Prove(OwnedProcessInvariantKind.RequiredContainment);
            _proof.RecordFact(new OwnedProcessFact(
                OwnedProcessFactKind.ContainmentEstablished,
                OwnedProcessFailurePhase.OwnershipEstablishment,
                ready.Ownership.ContainmentKind.ToString()));

            await WriteOperationFrameAsync(
                    new AuthorizeLaunchFrame(launchSpec),
                    cancellationToken)
                .ConfigureAwait(false);
            var started = await ReadOperationFrameAsync<TargetStartedFrame>(cancellationToken)
                .ConfigureAwait(false);
            _targetProcessId = started.Started.ProcessId;
            _proof.RecordFact(new OwnedProcessFact(
                OwnedProcessFactKind.TargetStarted,
                OwnedProcessFailurePhase.Start,
                started.Started.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        catch (Exception failure)
        {
            var outcome = await CompleteInitializationFailureAsync(failure).ConfigureAwait(false);
            throw new OwnedProcessExecutionException(outcome);
        }
    }

    private async Task WriteOperationFrameAsync(
        SupervisorProtocolFrame frame,
        CancellationToken cancellationToken)
    {
        var error = await _budget.AwaitOperationAsync(
                SupervisorProtocolCodec.WriteAsync(_commands, frame, cancellationToken).AsTask(),
                cancellationToken)
            .ConfigureAwait(false);
        if (error != null)
        {
            throw new InvalidDataException(error.Message);
        }
    }

    private async Task<TFrame> ReadOperationFrameAsync<TFrame>(
        CancellationToken cancellationToken)
        where TFrame : SupervisorProtocolFrame
    {
        var result = await _budget.AwaitOperationAsync(
                SupervisorProtocolCodec.ReadAsync(_status, cancellationToken).AsTask(),
                cancellationToken)
            .ConfigureAwait(false);
        if (result is SupervisorProtocolFrameRead { Frame: TFrame frame })
        {
            return frame;
        }

        var message = result switch
        {
            SupervisorProtocolReadRejected rejected => rejected.Error.Message,
            SupervisorProtocolFrameRead unexpected =>
                $"Unexpected supervisor frame {unexpected.Frame.Kind}.",
            _ => "The supervisor status channel closed before the expected frame."
        };
        throw new InvalidDataException(message);
    }

    private static Process StartSupervisor(string commandPipeName, string statusPipeName)
    {
        var assemblyPath = Assembly.GetExecutingAssembly().Location;
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveDotnetHost(),
            WorkingDirectory = Path.GetDirectoryName(assemblyPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(assemblyPath);
        startInfo.ArgumentList.Add(HostMode);
        startInfo.ArgumentList.Add(commandPipeName);
        startInfo.ArgumentList.Add(statusPipeName);
        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("The inert supervisor did not start.");
            }
            process.StandardInput.Close();
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    internal static SupervisorPipeNames CreatePipeNames(Guid leaseId)
    {
        var token = Convert.ToBase64String(leaseId.ToByteArray())
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return new SupervisorPipeNames($"c{token}", $"s{token}");
    }

    private static string ResolveDotnetHost()
    {
        var configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return string.IsNullOrWhiteSpace(configured) ? "dotnet" : configured;
    }

    private static async Task CleanupUnattachedSupervisorAsync(
        Process supervisor,
        NamedPipeServerStream commands,
        NamedPipeServerStream status,
        TransitionBudget budget)
    {
        await commands.DisposeAsync().ConfigureAwait(false);
        await status.DisposeAsync().ConfigureAwait(false);
        try
        {
            if (!supervisor.HasExited)
            {
                supervisor.Kill(entireProcessTree: true);
            }
            await budget.AwaitCleanupAsync(
                    supervisor.WaitForExitAsync(),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            supervisor.Dispose();
        }
    }

    private static OwnedProcessExecutionException CreatePrelaunchFailure(
        Exception failure,
        TransitionBudget budget)
    {
        var proof = new OwnedProcessProofAccumulator();
        var typed = ClassifyFailure(
            failure,
            OwnedProcessFailurePhase.OwnershipEstablishment,
            OwnedProcessFailureChannel.Operation);
        proof.Violate(OwnedProcessInvariantKind.RequiredContainment, typed);
        proof.Violate(
            OwnedProcessInvariantKind.TargetTerminal,
            CreateFailure(
                OwnedProcessFailureKind.TargetExecutionFailed,
                OwnedProcessFailurePhase.Start,
                OwnedProcessFailureChannel.Operation,
                failure));
        proof.Violate(OwnedProcessInvariantKind.OperationCompletion, typed);
        if (budget.OperationExpired || failure is TimeoutException)
        {
            proof.Violate(
                OwnedProcessInvariantKind.OperationBudget,
                CreateFailure(
                    OwnedProcessFailureKind.OperationDeadlineExceeded,
                    OwnedProcessFailurePhase.Start,
                    OwnedProcessFailureChannel.Operation,
                    failure));
        }
        else
        {
            proof.Prove(OwnedProcessInvariantKind.OperationBudget);
        }
        proof.Prove(OwnedProcessInvariantKind.TreeQuiescence);
        proof.Prove(OwnedProcessInvariantKind.BoundedCleanup);
        proof.Prove(OwnedProcessInvariantKind.StreamDrain);
        proof.Prove(OwnedProcessInvariantKind.OwnershipLifetime);
        var snapshot = proof.Snapshot();
        return new OwnedProcessExecutionException(new OwnedProcessOutcome(
            0,
            null,
            null,
            string.Empty,
            string.Empty,
            null,
            UnestablishedOwnership,
            snapshot.Invariants,
            snapshot.Facts,
            snapshot.Failures));
    }

    internal readonly record struct SupervisorPipeNames(
        string CommandPipeName,
        string StatusPipeName);
}
