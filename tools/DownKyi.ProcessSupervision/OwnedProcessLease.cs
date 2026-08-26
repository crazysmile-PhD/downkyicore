using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;

namespace DownKyi.ProcessSupervision;

#pragma warning disable CA1515 // The executable supervisor intentionally exports the lease API.

public sealed class OwnedProcessLease : IAsyncDisposable
{
    private const byte LaunchAuthorization = 0xC1;
    private const byte OwnershipEstablished = 0xA1;
    private const byte OwnershipMutationActive = 0xA2;
    private const int MaximumLaunchPayloadBytes = 1024 * 1024;

    private readonly Process _supervisor;
    private readonly IProcessContainmentLease _containment;
    private readonly Task<string> _standardOutput;
    private readonly Task<string> _standardError;
    private readonly TransitionBudget _budget;
    private readonly ProcessOwnershipMutation _mutation;
    private int _completionState;
    private int _reapFailureInjected;

    private OwnedProcessLease(
        Process supervisor,
        IProcessContainmentLease containment,
        Task<string> standardOutput,
        Task<string> standardError,
        TransitionBudget budget,
        ProcessOwnershipMutation mutation,
        int targetProcessId,
        ProcessOwnershipMetadata ownership)
    {
        _supervisor = supervisor;
        _containment = containment;
        _standardOutput = standardOutput;
        _standardError = standardError;
        _budget = budget;
        _mutation = mutation;
        TargetProcessId = targetProcessId;
        Ownership = ownership;
    }

    public ProcessOwnershipMetadata Ownership { get; }

    public int SupervisorProcessId => _supervisor.Id;

    public int TargetProcessId { get; }

    public static Task<OwnedProcessLease> StartAsync(
        LaunchSpec launchSpec,
        TransitionBudget budget,
        CancellationToken cancellationToken = default)
    {
        return StartCoreAsync(
            launchSpec,
            budget,
            ProcessOwnershipMutation.None,
            cancellationToken);
    }

    internal static Task<OwnedProcessLease> StartForTestingAsync(
        LaunchSpec launchSpec,
        TransitionBudget budget,
        ProcessOwnershipMutation mutation,
        CancellationToken cancellationToken = default)
    {
        return StartCoreAsync(launchSpec, budget, mutation, cancellationToken);
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The lease owner must preserve any execution failure while attempting bounded cleanup.")]
    public async Task<OwnedProcessOutcome> WaitAsync(
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _completionState, 1, 0) != 0)
        {
            throw new InvalidOperationException("The owned process lease has already completed.");
        }

        OwnedProcessOutcome? outcome = null;
        Exception? operationFailure = null;
        var pendingFailureKind = OwnedProcessFailureKind.ExecutionFailed;
        var supervisorProcessId = SupervisorProcessId;
        var treeQuiescenceProven = false;
        try
        {
            pendingFailureKind = OwnedProcessFailureKind.OperationDeadlineExceeded;
            await WaitForSupervisorExitAsync(
                    useCleanupBudget: false,
                    cancellationToken)
                .ConfigureAwait(false);
            pendingFailureKind = OwnedProcessFailureKind.OwnedTreeNotQuiescent;
            await WaitForTreeQuiescenceAsync(
                    useCleanupBudget: false,
                    cancellationToken)
                .ConfigureAwait(false);
            treeQuiescenceProven = true;
            pendingFailureKind = OwnedProcessFailureKind.StreamDrainDeadlineExceeded;
            await WaitWithBudgetAsync(
                    Task.WhenAll(_standardOutput, _standardError),
                    _budget.RemainingOperation,
                    "Owned process streams did not drain before the operation deadline.",
                    cancellationToken)
                .ConfigureAwait(false);

            outcome = new OwnedProcessOutcome(
                supervisorProcessId,
                TargetProcessId,
                _supervisor.ExitCode,
                await _standardOutput.ConfigureAwait(false),
                await _standardError.ConfigureAwait(false),
                TreeQuiescent: true,
                Ownership);
        }
        catch (Exception failure)
        {
            operationFailure = failure;
            if (failure is OperationCanceledException)
            {
                pendingFailureKind = OwnedProcessFailureKind.CallerCancelled;
            }
            else if (failure is not TimeoutException)
            {
                pendingFailureKind = OwnedProcessFailureKind.ExecutionFailed;
            }
        }

        if (operationFailure != null)
        {
            var cleanupFailure = await CaptureFailureAsync(TerminateAndReapAsync)
                .ConfigureAwait(false);
            var cleanupFailures = cleanupFailure == null
                ? Array.Empty<Exception>()
                : FlattenFailures(cleanupFailure).ToArray();
            var standardOutput = cleanupFailures.Length == 0
                ? await _standardOutput.ConfigureAwait(false)
                : ReadCompletedOutput(_standardOutput);
            var standardError = cleanupFailures.Length == 0
                ? await _standardError.ConfigureAwait(false)
                : ReadCompletedOutput(_standardError);
            throw new OwnedProcessExecutionException(
                new OwnedProcessFailure(
                    pendingFailureKind,
                    supervisorProcessId,
                    TargetProcessId,
                    standardOutput,
                    standardError,
                    TreeQuiescent: treeQuiescenceProven,
                    Ownership),
                operationFailure,
                cleanupFailures);
        }

        ReleaseResources();
        return outcome
            ?? throw new InvalidOperationException("The owned process outcome is unavailable.");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _completionState, 2, 0) == 0)
        {
            await TerminateAndReapAsync().ConfigureAwait(false);
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The launch owner must preserve any launch failure while attempting every containment cleanup stage.")]
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Successful launch transfers both disposable owners to the returned lease; every failed launch disposes both before propagating.")]
    private static async Task<OwnedProcessLease> StartCoreAsync(
        LaunchSpec launchSpec,
        TransitionBudget budget,
        ProcessOwnershipMutation mutation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launchSpec);
        ArgumentNullException.ThrowIfNull(budget);
        cancellationToken.ThrowIfCancellationRequested();

        // macOS implements named pipes with Unix-domain sockets whose full path is capped at 104 bytes.
        var controlPipeName = $"dkc-{Guid.NewGuid():N}";
        var statusPipeName = $"dks-{Guid.NewGuid():N}";
        using var control = new NamedPipeServerStream(
            controlPipeName,
            PipeDirection.Out,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var status = new NamedPipeServerStream(
            statusPipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var jobName = $"Local\\DownKyi.ProcessLease.{Guid.NewGuid():N}";
        var startInfo = CreateSupervisorStartInfo(
            controlPipeName,
            statusPipeName,
            jobName,
            mutation);
        var supervisor = new Process { StartInfo = startInfo };
        IProcessContainmentLease? containment = null;
        var started = false;
        try
        {
            if (!supervisor.Start())
            {
                throw new InvalidOperationException("The process supervisor did not start.");
            }

            started = true;
            var standardOutput = supervisor.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var standardError = supervisor.StandardError.ReadToEndAsync(CancellationToken.None);
            containment = PlatformProcessContainment.Create(supervisor, jobName, mutation);
            await WaitWithBudgetAsync(
                    Task.WhenAll(
                        control.WaitForConnectionAsync(cancellationToken),
                        status.WaitForConnectionAsync(cancellationToken)),
                    budget.RemainingOperation,
                    "The process supervisor did not connect its control channels before the deadline.",
                    cancellationToken)
                .ConfigureAwait(false);
            var payload = JsonSerializer.SerializeToUtf8Bytes(
                LaunchSpecPayload.FromLaunchSpec(launchSpec));
            if (payload.Length > MaximumLaunchPayloadBytes)
            {
                throw new InvalidOperationException("The immutable launch specification is too large.");
            }

            using (var writer = new BinaryWriter(control, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(LaunchAuthorization);
                writer.Write(payload.Length);
                writer.Write(payload);
                writer.Flush();
            }

            var decision = await ReadLaunchDecisionAsync(
                    status,
                    budget,
                    cancellationToken)
                .ConfigureAwait(false);
            if (decision.Status is not (
                    OwnershipEstablished or OwnershipMutationActive))
            {
                throw new InvalidOperationException(
                    "The process supervisor refused to launch without established ownership.");
            }

            var ownership = containment.Metadata with
            {
                OwnershipEstablished = decision.Status == OwnershipEstablished
            };
            return new OwnedProcessLease(
                supervisor,
                containment,
                standardOutput,
                standardError,
                budget,
                mutation,
                decision.TargetProcessId,
                ownership);
        }
        catch (Exception startFailure)
        {
            var failures = new Collection<Exception> { startFailure };
            try
            {
                containment?.Terminate();
            }
            catch (Exception cleanupFailure)
            {
                failures.Add(cleanupFailure);
            }

            try
            {
                if (started && !supervisor.HasExited)
                {
                    supervisor.Kill();
                    await WaitWithBudgetAsync(
                            supervisor.WaitForExitAsync(CancellationToken.None),
                            budget.RemainingCleanup,
                            "The failed process supervisor did not terminate before its hard deadline.",
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception cleanupFailure)
            {
                failures.Add(cleanupFailure);
            }

            try
            {
                containment?.Dispose();
            }
            catch (Exception cleanupFailure)
            {
                failures.Add(cleanupFailure);
            }
            try
            {
                supervisor.Dispose();
            }
            catch (Exception cleanupFailure)
            {
                failures.Add(cleanupFailure);
            }
            if (failures.Count == 1)
            {
                ExceptionDispatchInfo.Capture(startFailure).Throw();
            }

            throw new AggregateException(
                "Process launch and ownership cleanup both failed.",
                failures);
        }
    }

    private static ProcessStartInfo CreateSupervisorStartInfo(
        string controlPipeName,
        string statusPipeName,
        string jobName,
        ProcessOwnershipMutation mutation)
    {
        var assemblyPath = typeof(OwnedProcessLease).Assembly.Location;
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetDirectoryName(assemblyPath)
                ?? throw new InvalidOperationException("The supervisor directory is unavailable."),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(assemblyPath);
        startInfo.ArgumentList.Add(SupervisorHost.HostArgument);
        startInfo.ArgumentList.Add(controlPipeName);
        startInfo.ArgumentList.Add(statusPipeName);
        startInfo.ArgumentList.Add(jobName);
        startInfo.ArgumentList.Add(((int)mutation).ToString(System.Globalization.CultureInfo.InvariantCulture));
        return startInfo;
    }

    private static async Task<LaunchDecision> ReadLaunchDecisionAsync(
        Stream status,
        TransitionBudget budget,
        CancellationToken cancellationToken)
    {
        var payload = new byte[sizeof(byte) + sizeof(int)];
        var offset = 0;
        while (offset < payload.Length)
        {
            var read = await WaitWithBudgetAsync(
                    status.ReadAsync(
                            payload.AsMemory(offset, payload.Length - offset),
                            cancellationToken)
                        .AsTask(),
                    budget.RemainingOperation,
                    "The process supervisor did not establish ownership before the deadline.",
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new InvalidOperationException(
                    "The process supervisor closed its ownership channel before target launch.");
            }

            offset += read;
        }

        var targetProcessId = BitConverter.ToInt32(payload, sizeof(byte));
        if (targetProcessId <= 0)
        {
            throw new InvalidOperationException(
                "The process supervisor returned an invalid target identity.");
        }

        return new LaunchDecision(payload[0], targetProcessId);
    }

    private async Task WaitForSupervisorExitAsync(
        bool useCleanupBudget,
        CancellationToken cancellationToken)
    {
        await WaitWithBudgetAsync(
                _supervisor.WaitForExitAsync(cancellationToken),
                useCleanupBudget ? _budget.RemainingCleanup : _budget.RemainingOperation,
                useCleanupBudget
                    ? "The process supervisor did not reap before its hard deadline."
                    : "The owned process exceeded its operation deadline.",
                cancellationToken)
            .ConfigureAwait(false);
        if (_mutation.HasFlag(ProcessOwnershipMutation.FailAfterRootReap) &&
            Interlocked.Exchange(ref _reapFailureInjected, 1) == 0)
        {
            throw new InvalidOperationException("Injected root reap failure.");
        }
    }

    private async Task WaitForTreeQuiescenceAsync(
        bool useCleanupBudget,
        CancellationToken cancellationToken)
    {
        while (!_containment.IsTreeQuiescent())
        {
            var remaining = useCleanupBudget
                ? _budget.RemainingCleanup
                : _budget.RemainingOperation;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException(
                    useCleanupBudget
                        ? "The owned process tree did not become quiescent before its hard deadline."
                        : "The owned process tree did not become quiescent before the operation deadline.");
            }

            await Task.Delay(
                    remaining < TimeSpan.FromMilliseconds(20)
                        ? remaining
                        : TimeSpan.FromMilliseconds(20),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The lease owner must attempt every cleanup stage and preserve all concurrent failures.")]
    private async Task TerminateAndReapAsync()
    {
        var failures = new Collection<Exception>();
        var directRootTerminationRequired = !Ownership.OwnershipEstablished;
        try
        {
            if (!_containment.IsTreeQuiescent())
            {
                _containment.Terminate();
            }
        }
        catch (Exception failure)
        {
            failures.Add(failure);
            directRootTerminationRequired = true;
        }

        try
        {
            if (directRootTerminationRequired && !_supervisor.HasExited)
            {
                _supervisor.Kill();
            }
            await WaitForSupervisorExitAsync(
                    useCleanupBudget: true,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            failures.Add(failure);
        }

        try
        {
            await WaitForTreeQuiescenceAsync(
                    useCleanupBudget: true,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            failures.Add(failure);
        }

        try
        {
            await WaitWithBudgetAsync(
                    Task.WhenAll(_standardOutput, _standardError),
                    _budget.RemainingCleanup,
                    "Owned process streams did not drain before the hard deadline.",
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            failures.Add(failure);
        }

        try
        {
            ReleaseResources();
        }
        catch (Exception failure)
        {
            failures.Add(failure);
        }
        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }
        if (failures.Count > 1)
        {
            throw new AggregateException("Owned process cleanup encountered multiple failures.", failures);
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The lease must attempt both containment and process-handle disposal and preserve both failures.")]
    private void ReleaseResources()
    {
        var failures = new Collection<Exception>();
        try
        {
            _containment.Dispose();
        }
        catch (Exception failure)
        {
            failures.Add(failure);
        }
        try
        {
            _supervisor.Dispose();
        }
        catch (Exception failure)
        {
            failures.Add(failure);
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }
        if (failures.Count > 1)
        {
            throw new AggregateException("Owned process resource release failed.", failures);
        }
    }

    private static async Task<T> WaitWithBudgetAsync<T>(
        Task<T> operation,
        TimeSpan remaining,
        string timeoutMessage,
        CancellationToken cancellationToken)
    {
        if (remaining <= TimeSpan.Zero)
        {
            throw new TimeoutException(timeoutMessage);
        }

        try
        {
            return await operation.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException failure)
        {
            throw new TimeoutException(timeoutMessage, failure);
        }
    }

    private static async Task WaitWithBudgetAsync(
        Task operation,
        TimeSpan remaining,
        string timeoutMessage,
        CancellationToken cancellationToken)
    {
        if (remaining <= TimeSpan.Zero)
        {
            throw new TimeoutException(timeoutMessage);
        }

        try
        {
            await operation.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException failure)
        {
            throw new TimeoutException(timeoutMessage, failure);
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "This helper returns any cleanup failure so the caller can preserve it with the causal execution failure.")]
    private static async Task<Exception?> CaptureFailureAsync(Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(false);
            return null;
        }
        catch (Exception failure)
        {
            return failure;
        }
    }

    private static string ReadCompletedOutput(Task<string> output)
    {
        return output.Status == TaskStatus.RanToCompletion
            ? output.Result
            : string.Empty;
    }

    private static IEnumerable<Exception> FlattenFailures(Exception failure)
    {
        return failure is AggregateException aggregate
            ? aggregate.Flatten().InnerExceptions
            : new[] { failure };
    }

    private sealed record LaunchSpecPayload(
        string FileName,
        IReadOnlyList<string> Arguments,
        string WorkingDirectory,
        IReadOnlyDictionary<string, string?> Environment,
        bool CloseStandardInput)
    {
        public static LaunchSpecPayload FromLaunchSpec(LaunchSpec launchSpec)
        {
            return new LaunchSpecPayload(
                launchSpec.FileName,
                launchSpec.Arguments,
                launchSpec.WorkingDirectory,
                launchSpec.Environment,
                launchSpec.CloseStandardInput);
        }
    }

    private sealed record LaunchDecision(byte Status, int TargetProcessId);
}
