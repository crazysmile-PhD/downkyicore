using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

#pragma warning disable CA1515 // The executable supervisor intentionally exports restart-handoff contracts to production owners.

namespace DownKyi.ProcessSupervision;

public sealed class RestartHandoffLease : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly Process _helper;
    private readonly NamedPipeServerStream _authorization;
    private readonly NamedPipeServerStream _status;
    private readonly TransitionBudget _budget;
    private readonly RestartHandoffDeadline _deadline;
    private readonly byte[] _nonce;
    private readonly int _helperProcessId;
    private int _completion;
    private bool _resourcesReleased;

    private RestartHandoffLease(
        Process helper,
        NamedPipeServerStream authorization,
        NamedPipeServerStream status,
        TransitionBudget budget,
        RestartHandoffDeadline deadline,
        byte[] nonce,
        ProcessIdentityAuthority identityAuthority)
    {
        _helper = helper;
        _authorization = authorization;
        _status = status;
        _budget = budget;
        _deadline = deadline;
        _nonce = nonce;
        _helperProcessId = helper.Id;
        ParentIdentityAuthority = identityAuthority;
        State = RestartHandoffState.Authorized;
    }

    public RestartHandoffState State { get; private set; }

    public ProcessIdentityAuthority ParentIdentityAuthority { get; }

    public int HelperProcessId => _helperProcessId;

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Preparation must preserve its causal failure and every bounded helper cleanup failure.")]
    public static async Task<RestartHandoffLease> PrepareAsync(
        ProcessStartInfo helperStartInfo,
        int parentProcessId,
        TransitionBudget budget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(helperStartInfo);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parentProcessId);
        ArgumentNullException.ThrowIfNull(budget);
        cancellationToken.ThrowIfCancellationRequested();
        if (helperStartInfo.UseShellExecute)
        {
            throw new ArgumentException(
                "Restart helper launch cannot use shell execution.",
                nameof(helperStartInfo));
        }

        var deadline = budget.CreateRestartHandoffDeadline();
        var authorizationName = IpcEndpointName.Create("RestartHandoff.Authorization");
        var statusName = IpcEndpointName.Create("RestartHandoff.Status");
        var nonce = RandomNumberGenerator.GetBytes(RestartHandoffProtocol.NonceLength);
        AppendProtocolArguments(
            helperStartInfo,
            parentProcessId,
            authorizationName.PhysicalIdentifier,
            statusName.PhysicalIdentifier,
            deadline,
            nonce);

        var pipeOptions = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
        var authorization = new NamedPipeServerStream(
            authorizationName.PhysicalIdentifier,
            PipeDirection.Out,
            1,
            PipeTransmissionMode.Byte,
            pipeOptions);
        var status = new NamedPipeServerStream(
            statusName.PhysicalIdentifier,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            pipeOptions);
        Process? helper = null;
        int? helperProcessId = null;
        try
        {
            helper = Process.Start(helperStartInfo)
                ?? throw new InvalidOperationException(
                    "The restart successor candidate could not be started.");
            helperProcessId = helper.Id;
            var connections = Task.WhenAll(
                authorization.WaitForConnectionAsync(cancellationToken),
                status.WaitForConnectionAsync(cancellationToken));
            await AwaitPreparationStepAsync(
                    connections,
                    helper,
                    budget,
                    cancellationToken)
                .ConfigureAwait(false);
            var ready = await ReadReadyStatusAsync(
                    status,
                    helper,
                    nonce,
                    budget,
                    cancellationToken)
                .ConfigureAwait(false);
            if (ready.State != RestartHandoffState.Authorized ||
                ready.IdentityAuthority == null)
            {
                throw CreateException(
                    ready.FailureKind ?? RestartHandoffFailureKind.WatcherFailed,
                    ready.State,
                    ready.IdentityAuthority,
                    helper.Id,
                    ready.Detail ?? "The helper did not publish watcher readiness.");
            }

            return new RestartHandoffLease(
                helper,
                authorization,
                status,
                budget,
                deadline,
                nonce,
                ready.IdentityAuthority.Value);
        }
        catch (Exception failure)
        {
            var cleanupFailures = await CleanupCandidateAsync(
                    helper,
                    authorization,
                    status,
                    budget)
                .ConfigureAwait(false);
            if (failure is OperationCanceledException &&
                cancellationToken.IsCancellationRequested)
            {
                if (cleanupFailures.Count == 0)
                {
                    ExceptionDispatchInfo.Capture(failure).Throw();
                }

                throw new AggregateException(
                    "Restart handoff preparation was cancelled and candidate cleanup failed.",
                    new[] { failure }.Concat(cleanupFailures));
            }

            if (failure is RestartHandoffException typed)
            {
                throw new RestartHandoffException(
                    typed.Failure,
                    typed.InnerException ?? typed,
                    typed.CleanupFailures.Concat(cleanupFailures).ToArray());
            }

            var kind = failure switch
            {
                TimeoutException => RestartHandoffFailureKind.DeadlineExceeded,
                EndOfStreamException => RestartHandoffFailureKind.HelperCrashed,
                _ => RestartHandoffFailureKind.PrepareFailed
            };
            throw new RestartHandoffException(
                new RestartHandoffFailure(
                    kind,
                    RestartHandoffState.Prepared,
                    null,
                    helperProcessId,
                    failure.Message),
                failure,
                cleanupFailures);
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Commit must preserve a channel failure together with any concurrent channel-disposal failure.")]
    public void Commit()
    {
        lock (_sync)
        {
            if (_completion != 0)
            {
                throw new InvalidOperationException(
                    "The restart handoff transaction has already completed.");
            }

            if (_budget.RemainingOperation <= TimeSpan.Zero)
            {
                State = RestartHandoffState.Failed;
                _completion = 3;
                throw CreateException(
                    RestartHandoffFailureKind.DeadlineExceeded,
                    RestartHandoffState.Authorized,
                    ParentIdentityAuthority,
                    _helperProcessId,
                    "The restart handoff deadline expired before commit.");
            }

            try
            {
                var frame = RestartAuthorizationFrame.Create(_deadline, _nonce);
                _authorization.Write(frame);
                _authorization.Flush();
                _authorization.Dispose();
                State = RestartHandoffState.Committed;
                _completion = 1;
            }
            catch (Exception failure) when (failure is IOException or ObjectDisposedException
                or InvalidOperationException)
            {
                State = RestartHandoffState.Failed;
                _completion = 3;
                var cleanupFailures = new List<Exception>();
                try
                {
                    _authorization.Dispose();
                }
                catch (Exception cleanupFailure)
                {
                    cleanupFailures.Add(cleanupFailure);
                }

                throw new RestartHandoffException(
                    new RestartHandoffFailure(
                        RestartHandoffFailureKind.AuthorizationFailed,
                        RestartHandoffState.Authorized,
                        ParentIdentityAuthority,
                        _helperProcessId,
                        failure.Message),
                    failure,
                    cleanupFailures);
            }
        }
    }

    public async Task RevokeAsync()
    {
        lock (_sync)
        {
            if (_completion != 0)
            {
                throw new InvalidOperationException(
                    "The restart handoff transaction has already completed.");
            }

            _completion = 2;
            State = RestartHandoffState.Revoked;
        }

        var failures = await CleanupOwnedHelperAsync(terminate: true).ConfigureAwait(false);
        ThrowRevocationFailures(failures);
    }

    public async ValueTask DisposeAsync()
    {
        bool terminate;
        lock (_sync)
        {
            if (_resourcesReleased)
            {
                return;
            }

            terminate = _completion is 0 or 3;
            if (_completion == 0)
            {
                _completion = 2;
                State = RestartHandoffState.Revoked;
            }
        }

        var failures = await CleanupOwnedHelperAsync(terminate).ConfigureAwait(false);
        if (terminate)
        {
            ThrowRevocationFailures(failures);
        }
        else if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }
        else if (failures.Count > 1)
        {
            throw new AggregateException(
                "Restart handoff resource release encountered multiple failures.",
                failures);
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Revocation must attempt every helper cleanup stage and preserve concurrent failures.")]
    private async Task<IReadOnlyList<Exception>> CleanupOwnedHelperAsync(bool terminate)
    {
        lock (_sync)
        {
            if (_resourcesReleased)
            {
                return [];
            }

            _resourcesReleased = true;
        }

        var failures = new List<Exception>();
        try
        {
            await _authorization.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            failures.Add(failure);
        }

        try
        {
            await _status.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            failures.Add(failure);
        }

        if (terminate)
        {
            try
            {
                if (!_helper.HasExited)
                {
                    _helper.Kill();
                }
            }
            catch (Exception failure)
            {
                failures.Add(failure);
            }

            try
            {
                var remaining = _budget.RemainingCleanup;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException(
                        "The restart helper cleanup deadline expired before reap.");
                }

                await _helper.WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(remaining)
                    .ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                failures.Add(failure);
            }
        }

        try
        {
            _helper.Dispose();
        }
        catch (Exception failure)
        {
            failures.Add(failure);
        }

        return failures;
    }

    private void ThrowRevocationFailures(IReadOnlyList<Exception> failures)
    {
        if (failures.Count == 0)
        {
            return;
        }

        throw new RestartHandoffException(
            new RestartHandoffFailure(
                RestartHandoffFailureKind.RevocationFailed,
                RestartHandoffState.Revoked,
                ParentIdentityAuthority,
                _helperProcessId,
                "The prepared restart helper did not revoke cleanly."),
            failures[0],
            failures);
    }

    private static void AppendProtocolArguments(
        ProcessStartInfo startInfo,
        int parentProcessId,
        string authorizationEndpoint,
        string statusEndpoint,
        RestartHandoffDeadline deadline,
        byte[] nonce)
    {
        var arguments = new[]
        {
            RestartHandoffProtocol.MarkerArgument,
            RestartHandoffProtocol.ParentProcessIdArgument,
            parentProcessId.ToString(CultureInfo.InvariantCulture),
            RestartHandoffProtocol.AuthorizationEndpointArgument,
            authorizationEndpoint,
            RestartHandoffProtocol.StatusEndpointArgument,
            statusEndpoint,
            RestartHandoffProtocol.DeadlineDomainArgument,
            deadline.Domain,
            RestartHandoffProtocol.OperationExpiryArgument,
            deadline.OperationExpiresAt.ToString(CultureInfo.InvariantCulture),
            RestartHandoffProtocol.CleanupExpiryArgument,
            deadline.CleanupExpiresAt.ToString(CultureInfo.InvariantCulture),
            RestartHandoffProtocol.ClockFrequencyArgument,
            deadline.Frequency.ToString(CultureInfo.InvariantCulture),
            RestartHandoffProtocol.NonceArgument,
            Convert.ToHexString(nonce)
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
    }

    private static async Task AwaitPreparationStepAsync(
        Task step,
        Process helper,
        TransitionBudget budget,
        CancellationToken cancellationToken)
    {
        var helperExit = helper.WaitForExitAsync(CancellationToken.None);
        var remaining = budget.RemainingOperation;
        if (remaining <= TimeSpan.Zero)
        {
            throw new TimeoutException(
                "The restart handoff deadline expired during preparation.");
        }

        var completed = await Task.WhenAny(step, helperExit)
            .WaitAsync(remaining, cancellationToken)
            .ConfigureAwait(false);
        if (completed == helperExit)
        {
            await helperExit.ConfigureAwait(false);
            throw new EndOfStreamException(
                $"The restart helper exited with {helper.ExitCode} before watcher readiness.");
        }

        await step.ConfigureAwait(false);
    }

    private static async Task<RestartReadyStatus> ReadReadyStatusAsync(
        NamedPipeServerStream status,
        Process helper,
        byte[] nonce,
        TransitionBudget budget,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            status,
            new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);
        var read = reader.ReadLineAsync(cancellationToken).AsTask();
        await AwaitPreparationStepAsync(read, helper, budget, cancellationToken)
            .ConfigureAwait(false);
        var line = await read.ConfigureAwait(false)
            ?? throw new EndOfStreamException(
                "The restart helper closed its status channel before watcher readiness.");
        RestartReadyStatus ready;
        try
        {
            ready = JsonSerializer.Deserialize<RestartReadyStatus>(line, RestartJson.Options)
                ?? throw new InvalidDataException(
                    "The restart helper readiness payload was empty.");
        }
        catch (JsonException failure)
        {
            throw new InvalidDataException(
                "The restart helper readiness payload was malformed.",
                failure);
        }

        byte[] statusNonce;
        try
        {
            statusNonce = Convert.FromHexString(ready.Nonce);
        }
        catch (FormatException failure)
        {
            throw new InvalidDataException(
                "The restart helper readiness nonce was malformed.",
                failure);
        }

        if (!CryptographicOperations.FixedTimeEquals(nonce, statusNonce))
        {
            throw new InvalidDataException(
                "The restart helper readiness nonce did not match its prepared transaction.");
        }

        var replayRead = reader.ReadLineAsync(cancellationToken).AsTask();
        await AwaitPreparationStepAsync(replayRead, helper, budget, cancellationToken)
            .ConfigureAwait(false);
        if (await replayRead.ConfigureAwait(false) != null)
        {
            throw new InvalidDataException(
                "The restart helper replayed its readiness transition.");
        }

        return ready;
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Failed preparation must attempt every candidate cleanup stage and return all failures.")]
    private static async Task<IReadOnlyList<Exception>> CleanupCandidateAsync(
        Process? helper,
        NamedPipeServerStream authorization,
        NamedPipeServerStream status,
        TransitionBudget budget)
    {
        var failures = new List<Exception>();
        try
        {
            await authorization.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            failures.Add(failure);
        }

        try
        {
            await status.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            failures.Add(failure);
        }

        if (helper != null)
        {
            try
            {
                if (!helper.HasExited)
                {
                    helper.Kill();
                }
            }
            catch (Exception failure)
            {
                failures.Add(failure);
            }

            try
            {
                var remaining = budget.RemainingCleanup;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException(
                        "The restart candidate cleanup deadline expired before reap.");
                }

                await helper.WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(remaining)
                    .ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                failures.Add(failure);
            }

            try
            {
                helper.Dispose();
            }
            catch (Exception failure)
            {
                failures.Add(failure);
            }
        }

        return failures;
    }

    private static RestartHandoffException CreateException(
        RestartHandoffFailureKind kind,
        RestartHandoffState state,
        ProcessIdentityAuthority? identityAuthority,
        int? helperProcessId,
        string detail)
    {
        return new RestartHandoffException(new RestartHandoffFailure(
            kind,
            state,
            identityAuthority,
            helperProcessId,
            detail));
    }
}

public static class RestartHandoffHelper
{
    public static Task<RestartHandoffOutcome> ExecuteAsync(
        RestartHandoffRequest request,
        ProcessStartInfo relaunchStartInfo,
        CancellationToken cancellationToken = default)
    {
        return ExecuteCoreAsync(
            request,
            relaunchStartInfo,
            cleanupFailureForTesting: null,
            cancellationToken);
    }

    internal static Task<RestartHandoffOutcome> ExecuteWithCleanupFailureForTestingAsync(
        RestartHandoffRequest request,
        ProcessStartInfo relaunchStartInfo,
        Func<RestartHandoffCleanupStage, Exception?> cleanupFailureForTesting,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cleanupFailureForTesting);
        return ExecuteCoreAsync(
            request,
            relaunchStartInfo,
            cleanupFailureForTesting,
            cancellationToken);
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The helper converts every terminal native, protocol and relaunch failure into typed evidence.")]
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The finally block independently disposes every acquired helper resource through the typed cleanup collector.")]
    private static async Task<RestartHandoffOutcome> ExecuteCoreAsync(
        RestartHandoffRequest request,
        ProcessStartInfo relaunchStartInfo,
        Func<RestartHandoffCleanupStage, Exception?>? cleanupFailureForTesting,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(relaunchStartInfo);
        if (relaunchStartInfo.UseShellExecute)
        {
            throw new ArgumentException(
                "Restart relaunch cannot use shell execution.",
                nameof(relaunchStartInfo));
        }

        var machine = new RestartHandoffStateMachine();
        ParentLifetimeLease? parent = null;
        NamedPipeClientStream? authorization = null;
        NamedPipeClientStream? status = null;
        ProcessIdentityAuthority? authority = null;
        var relaunchAttempts = 0;
        var cleanupFailures = new List<RestartHandoffCleanupFailure>();
        var readOnlyCleanupFailures = cleanupFailures.AsReadOnly();
        try
        {
            request.Deadline.ValidateCurrentClock();
            status = new NamedPipeClientStream(
                ".",
                request.StatusEndpoint,
                PipeDirection.Out,
                PipeOptions.Asynchronous);
            authorization = new NamedPipeClientStream(
                ".",
                request.AuthorizationEndpoint,
                PipeDirection.In,
                PipeOptions.Asynchronous);
            await ConnectWithinDeadlineAsync(status, request.Deadline, cancellationToken)
                .ConfigureAwait(false);
            await ConnectWithinDeadlineAsync(authorization, request.Deadline, cancellationToken)
                .ConfigureAwait(false);

            try
            {
                parent = ParentLifetimeLeaseFactory.Create(request.ParentProcessId);
                authority = parent.IdentityAuthority;
                if (parent.IsExited())
                {
                    await SendReadyStatusAsync(
                            status,
                            request.Nonce,
                            RestartHandoffState.Failed,
                            authority,
                            RestartHandoffFailureKind.ParentExitedBeforeReady,
                            "The exact parent exited before watcher readiness.",
                            request.Deadline,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return Failure(
                        RestartHandoffFailureKind.ParentExitedBeforeReady,
                        machine.State,
                        authority,
                        relaunchAttempts,
                        "The exact parent exited before watcher readiness.",
                        readOnlyCleanupFailures);
                }
            }
            catch (Exception failure)
            {
                await SendReadyStatusAsync(
                        status,
                        request.Nonce,
                        RestartHandoffState.Failed,
                        authority,
                        RestartHandoffFailureKind.WatcherFailed,
                        failure.Message,
                        request.Deadline,
                        cancellationToken)
                    .ConfigureAwait(false);
                return Failure(
                    RestartHandoffFailureKind.WatcherFailed,
                    machine.State,
                    authority,
                    relaunchAttempts,
                    failure.Message,
                    readOnlyCleanupFailures);
            }

            machine.Transition(RestartHandoffState.Prepared, RestartHandoffState.WatcherReady);
            machine.Transition(RestartHandoffState.WatcherReady, RestartHandoffState.Authorized);
            await SendReadyStatusAsync(
                    status,
                    request.Nonce,
                    RestartHandoffState.Authorized,
                    authority,
                    null,
                    null,
                    request.Deadline,
                    cancellationToken)
                .ConfigureAwait(false);
            await status.DisposeAsync().ConfigureAwait(false);

            var authorizationResult = await RestartAuthorizationFrame.ReadAsync(
                    authorization,
                    request.Deadline,
                    request.Nonce,
                    cancellationToken)
                .ConfigureAwait(false);
            if (authorizationResult != null)
            {
                machine.Transition(RestartHandoffState.Authorized, RestartHandoffState.Revoked);
                return Failure(
                    authorizationResult.Value.Kind,
                    machine.State,
                    authority,
                    relaunchAttempts,
                    authorizationResult.Value.Detail,
                    readOnlyCleanupFailures);
            }

            machine.Transition(RestartHandoffState.Authorized, RestartHandoffState.Committed);
            var parentOutcome = await parent.WaitForExitAsync(
                    request.Deadline,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!parentOutcome.ExactParentExited)
            {
                machine.Fail();
                return Failure(
                    RestartHandoffFailureKind.DeadlineExceeded,
                    machine.State,
                    authority,
                    relaunchAttempts,
                    "The exact parent did not exit before the prepared deadline.",
                    readOnlyCleanupFailures);
            }

            machine.Transition(RestartHandoffState.Committed, RestartHandoffState.ParentExited);
            if (request.Deadline.RemainingOperation <= TimeSpan.Zero)
            {
                machine.Fail();
                return Failure(
                    RestartHandoffFailureKind.DeadlineExceeded,
                    machine.State,
                    authority,
                    relaunchAttempts,
                    "The prepared deadline expired before relaunch.",
                    readOnlyCleanupFailures);
            }

            machine.Transition(
                RestartHandoffState.ParentExited,
                RestartHandoffState.RelaunchStarted);
            relaunchAttempts++;
            using (var replacement = Process.Start(relaunchStartInfo)
                ?? throw new InvalidOperationException(
                    "The replacement application could not be started."))
            {
            }

            machine.Transition(RestartHandoffState.RelaunchStarted, RestartHandoffState.Completed);
            return new RestartHandoffOutcome(
                machine.State,
                authority,
                relaunchAttempts,
                null)
            {
                CleanupFailures = readOnlyCleanupFailures
            };
        }
        catch (OperationCanceledException failure)
        {
            var kind = machine.State switch
            {
                RestartHandoffState.Committed => RestartHandoffFailureKind.ParentWaitFailed,
                RestartHandoffState.ParentExited or RestartHandoffState.RelaunchStarted =>
                    RestartHandoffFailureKind.RelaunchFailed,
                _ => RestartHandoffFailureKind.AuthorizationFailed
            };
            machine.Fail();
            return Failure(
                kind,
                machine.State,
                authority,
                relaunchAttempts,
                failure.Message,
                readOnlyCleanupFailures);
        }
        catch (TimeoutException failure)
        {
            machine.Fail();
            return Failure(
                RestartHandoffFailureKind.DeadlineExceeded,
                machine.State,
                authority,
                relaunchAttempts,
                failure.Message,
                readOnlyCleanupFailures);
        }
        catch (Exception failure)
        {
            var kind = machine.State switch
            {
                RestartHandoffState.Prepared or RestartHandoffState.WatcherReady =>
                    RestartHandoffFailureKind.WatcherFailed,
                RestartHandoffState.Authorized =>
                    RestartHandoffFailureKind.AuthorizationFailed,
                RestartHandoffState.Committed =>
                    RestartHandoffFailureKind.ParentWaitFailed,
                RestartHandoffState.ParentExited or RestartHandoffState.RelaunchStarted =>
                    RestartHandoffFailureKind.RelaunchFailed,
                _ => RestartHandoffFailureKind.HelperCrashed
            };
            machine.Fail();
            return Failure(
                kind,
                machine.State,
                authority,
                relaunchAttempts,
                failure.Message,
                readOnlyCleanupFailures);
        }
        finally
        {
            await CaptureCleanupFailureAsync(
                    status,
                    RestartHandoffCleanupStage.StatusEndpoint,
                    cleanupFailures,
                    cleanupFailureForTesting)
                .ConfigureAwait(false);
            await CaptureCleanupFailureAsync(
                    authorization,
                    RestartHandoffCleanupStage.AuthorizationEndpoint,
                    cleanupFailures,
                    cleanupFailureForTesting)
                .ConfigureAwait(false);
            await CaptureCleanupFailureAsync(
                    parent,
                    RestartHandoffCleanupStage.ParentLifetime,
                    cleanupFailures,
                    cleanupFailureForTesting)
                .ConfigureAwait(false);
        }
    }

    private static RestartHandoffOutcome Failure(
        RestartHandoffFailureKind kind,
        RestartHandoffState state,
        ProcessIdentityAuthority? authority,
        int relaunchAttempts,
        string detail,
        IReadOnlyList<RestartHandoffCleanupFailure> cleanupFailures)
    {
        return new RestartHandoffOutcome(
            state,
            authority,
            relaunchAttempts,
            new RestartHandoffFailure(
                kind,
                state,
                authority,
                Environment.ProcessId,
                detail))
        {
            CleanupFailures = cleanupFailures
        };
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Every helper cleanup stage must be attempted and retained independently.")]
    private static async Task CaptureCleanupFailureAsync(
        IAsyncDisposable? resource,
        RestartHandoffCleanupStage stage,
        List<RestartHandoffCleanupFailure> failures,
        Func<RestartHandoffCleanupStage, Exception?>? cleanupFailureForTesting)
    {
        if (resource == null)
        {
            return;
        }

        try
        {
            await resource.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            failures.Add(RestartHandoffCleanupFailure.FromException(stage, failure));
        }

        if (cleanupFailureForTesting == null)
        {
            return;
        }

        try
        {
            var injectedFailure = cleanupFailureForTesting(stage);
            if (injectedFailure != null)
            {
                throw injectedFailure;
            }
        }
        catch (Exception failure)
        {
            failures.Add(RestartHandoffCleanupFailure.FromException(stage, failure));
        }
    }

    private static async Task ConnectWithinDeadlineAsync(
        NamedPipeClientStream pipe,
        RestartHandoffDeadline deadline,
        CancellationToken cancellationToken)
    {
        var remaining = deadline.RemainingOperation;
        if (remaining <= TimeSpan.Zero)
        {
            throw new TimeoutException(
                "The restart handoff deadline expired before IPC connection.");
        }

        await pipe.ConnectAsync(cancellationToken)
            .WaitAsync(remaining, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task SendReadyStatusAsync(
        Stream status,
        byte[] nonce,
        RestartHandoffState state,
        ProcessIdentityAuthority? authority,
        RestartHandoffFailureKind? failureKind,
        string? detail,
        RestartHandoffDeadline deadline,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(
            new RestartReadyStatus(
                Convert.ToHexString(nonce),
                state,
                authority,
                failureKind,
                detail),
            RestartJson.Options) + "\n";
        var bytes = Encoding.UTF8.GetBytes(payload);
        var remaining = deadline.RemainingOperation;
        if (remaining <= TimeSpan.Zero)
        {
            throw new TimeoutException(
                "The restart handoff deadline expired before watcher readiness publication.");
        }

        await status.WriteAsync(bytes, cancellationToken)
            .AsTask()
            .WaitAsync(remaining, cancellationToken)
            .ConfigureAwait(false);
        await status.FlushAsync(cancellationToken)
            .WaitAsync(deadline.RemainingOperation, cancellationToken)
            .ConfigureAwait(false);
    }
}

internal static class RestartAuthorizationFrame
{
    private const string Magic = "DKYRST04";
    private const int Version = 1;
    private const byte CommitCommand = 1;
    private const int FrameLength = 8 + sizeof(int) + RestartHandoffProtocol.NonceLength +
        sizeof(long) + sizeof(long) + sizeof(long) + sizeof(byte);

    public static byte[] Create(RestartHandoffDeadline deadline, byte[] nonce)
    {
        var frame = new byte[FrameLength];
        Encoding.ASCII.GetBytes(Magic).CopyTo(frame, 0);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(8), Version);
        nonce.CopyTo(frame, 12);
        var offset = 12 + RestartHandoffProtocol.NonceLength;
        BinaryPrimitives.WriteInt64LittleEndian(
            frame.AsSpan(offset),
            deadline.OperationExpiresAt);
        offset += sizeof(long);
        BinaryPrimitives.WriteInt64LittleEndian(
            frame.AsSpan(offset),
            deadline.CleanupExpiresAt);
        offset += sizeof(long);
        BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(offset), deadline.Frequency);
        frame[^1] = CommitCommand;
        return frame;
    }

    public static async Task<(RestartHandoffFailureKind Kind, string Detail)?> ReadAsync(
        Stream authorization,
        RestartHandoffDeadline deadline,
        byte[] expectedNonce,
        CancellationToken cancellationToken)
    {
        using var payload = new MemoryStream();
        var buffer = new byte[FrameLength];
        while (true)
        {
            var remaining = deadline.RemainingOperation;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException(
                    "The restart handoff deadline expired during authorization.");
            }

            var read = await authorization.ReadAsync(buffer, cancellationToken)
                .AsTask()
                .WaitAsync(remaining, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await payload.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
            if (payload.Length > FrameLength * 2L)
            {
                return (
                    RestartHandoffFailureKind.AuthorizationRejected,
                    "The authorization payload exceeded its one-shot frame bound.");
            }
        }

        var bytes = payload.ToArray();
        if (bytes.Length == 0)
        {
            return (
                RestartHandoffFailureKind.CommitChannelClosed,
                "The authorization channel closed before commit.");
        }

        if (bytes.Length == FrameLength * 2 &&
            bytes.AsSpan(0, FrameLength).SequenceEqual(bytes.AsSpan(FrameLength)))
        {
            return (
                RestartHandoffFailureKind.AuthorizationRejected,
                "The authorization frame was replayed.");
        }

        if (bytes.Length != FrameLength)
        {
            return (
                RestartHandoffFailureKind.AuthorizationRejected,
                bytes.Length < FrameLength
                    ? "The authorization frame was partial."
                    : "The authorization payload contained more than one transition.");
        }

        var expected = Create(deadline, expectedNonce);
        if (!CryptographicOperations.FixedTimeEquals(expected, bytes))
        {
            return (
                RestartHandoffFailureKind.AuthorizationRejected,
                "The authorization frame did not match the prepared transaction.");
        }

        if (deadline.RemainingOperation <= TimeSpan.Zero)
        {
            return (
                RestartHandoffFailureKind.DeadlineExceeded,
                "The prepared deadline expired before commit acceptance.");
        }

        return null;
    }
}

internal sealed record RestartReadyStatus(
    string Nonce,
    RestartHandoffState State,
    ProcessIdentityAuthority? IdentityAuthority,
    RestartHandoffFailureKind? FailureKind,
    string? Detail);

internal static class RestartJson
{
    public static JsonSerializerOptions Options { get; } =
        new(JsonSerializerDefaults.Web);
}
