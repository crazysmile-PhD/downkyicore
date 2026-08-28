using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace DownKyi.ProcessSupervision;

#pragma warning disable CA1515 // The executable supervisor intentionally exports the collector API to PowerShell.

public static class OwnedDiagnosticCollector
{
    public static Task<DiagnosticCollectorOutcome> CollectAsync(
        DiagnosticCollectorRequest request,
        CancellationToken cancellationToken = default)
    {
        return CollectCoreAsync(
            request,
            DiagnosticCollectorMutation.None,
            cancellationToken);
    }

    internal static Task<DiagnosticCollectorOutcome> CollectForTestingAsync(
        DiagnosticCollectorRequest request,
        DiagnosticCollectorMutation mutation,
        CancellationToken cancellationToken = default)
    {
        return CollectCoreAsync(request, mutation, cancellationToken);
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The collector boundary must convert every start and ownership failure into its typed public contract.")]
    private static async Task<DiagnosticCollectorOutcome> CollectCoreAsync(
        DiagnosticCollectorRequest request,
        DiagnosticCollectorMutation mutation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (cancellationToken.IsCancellationRequested)
        {
            throw CreateFailure(
                DiagnosticCollectorFailureKind.CallerCancelled,
                CreateNotStartedEvidence(timedOut: false),
                new OperationCanceledException(cancellationToken),
                Array.Empty<DiagnosticCollectorCleanupFailure>());
        }
        if (request.Window.RemainingOperation <= TimeSpan.Zero)
        {
            throw CreateFailure(
                DiagnosticCollectorFailureKind.OperationDeadlineExceeded,
                CreateNotStartedEvidence(timedOut: true),
                new TimeoutException(
                    "The diagnostic collector window was exhausted before launch."),
                Array.Empty<DiagnosticCollectorCleanupFailure>());
        }

        var budget = mutation.HasFlag(DiagnosticCollectorMutation.IgnoreAllocatedWindow)
            ? request.Window.ParentBudget
            : request.Window.Budget;
        var processMutation = MapMutation(mutation);
        OwnedProcessLease lease;
        try
        {
            lease = processMutation == ProcessOwnershipMutation.None
                ? await OwnedProcessLease.StartAsync(
                        request.Launch,
                        budget,
                        cancellationToken)
                    .ConfigureAwait(false)
                : await OwnedProcessLease.StartForTestingAsync(
                        request.Launch,
                        budget,
                        processMutation,
                        cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            var (primary, cleanupFailures) = SplitStartFailure(failure);
            var kind = cancellationToken.IsCancellationRequested ||
                       primary is OperationCanceledException
                ? DiagnosticCollectorFailureKind.CallerCancelled
                : primary is TimeoutException ||
                  request.Window.RemainingOperation <= TimeSpan.Zero
                    ? DiagnosticCollectorFailureKind.OperationDeadlineExceeded
                    : DiagnosticCollectorFailureKind.StartFailed;
            throw CreateFailure(
                kind,
                CreateNotStartedEvidence(
                    timedOut: kind == DiagnosticCollectorFailureKind.OperationDeadlineExceeded),
                primary,
                cleanupFailures);
        }

        await using var leaseScope = lease.ConfigureAwait(false);
        try
        {
            var outcome = await lease.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new DiagnosticCollectorOutcome(
                new DiagnosticCollectorEvidence(
                    Started: true,
                    Exited: true,
                    Reaped: true,
                    StreamsDrained: true,
                    TimedOut: false,
                    outcome.ExitCode,
                    outcome.StandardOutput,
                    outcome.StandardError));
        }
        catch (OwnedProcessExecutionException failure)
        {
            var cleanupFailures = MapCleanupFailures(failure.CleanupStageFailures);
            var kind = MapFailureKind(failure.Failure.Kind);
            var reapFailed = cleanupFailures.Any(item => item.Kind is
                DiagnosticCollectorCleanupFailureKind.ReapDeadlineExceeded or
                DiagnosticCollectorCleanupFailureKind.ReapFailed);
            var drainFailed = cleanupFailures.Any(item =>
                item.Kind == DiagnosticCollectorCleanupFailureKind.StreamDrainDeadlineExceeded);
            var evidence = new DiagnosticCollectorEvidence(
                Started: true,
                Exited: failure.Failure.TargetExitedAtUnixMilliseconds.HasValue || !reapFailed,
                Reaped: !reapFailed,
                StreamsDrained:
                    failure.Failure.Kind != OwnedProcessFailureKind.StreamDrainDeadlineExceeded &&
                    !drainFailed,
                TimedOut: kind is
                    DiagnosticCollectorFailureKind.OperationDeadlineExceeded or
                    DiagnosticCollectorFailureKind.StreamDrainDeadlineExceeded,
                ExitCode: null,
                failure.Failure.StandardOutput,
                failure.Failure.StandardError);
            throw CreateFailure(
                kind,
                evidence,
                failure.InnerException ?? failure,
                cleanupFailures);
        }
        catch (Exception failure)
        {
            var cleanupFailures = new[]
            {
                new DiagnosticCollectorCleanupFailure(
                    DiagnosticCollectorCleanupFailureKind.DisposeFailed,
                    failure)
            };
            throw CreateFailure(
                DiagnosticCollectorFailureKind.CleanupFailed,
                new DiagnosticCollectorEvidence(
                    Started: true,
                    Exited: false,
                    Reaped: false,
                    StreamsDrained: false,
                    TimedOut: false,
                    ExitCode: null,
                    StandardOutput: string.Empty,
                    StandardError: string.Empty),
                failure,
                cleanupFailures);
        }
    }

    private static ProcessOwnershipMutation MapMutation(DiagnosticCollectorMutation mutation)
    {
        var processMutation = ProcessOwnershipMutation.None;
        if (mutation.HasFlag(DiagnosticCollectorMutation.FailAfterTerminate))
        {
            processMutation |= ProcessOwnershipMutation.FailAfterContainmentTermination;
        }
        if (mutation.HasFlag(DiagnosticCollectorMutation.StallReap))
        {
            processMutation |= ProcessOwnershipMutation.StallRootReap;
        }
        if (mutation.HasFlag(DiagnosticCollectorMutation.StallStreamDrain))
        {
            processMutation |= ProcessOwnershipMutation.StallStreamDrain;
        }

        return processMutation;
    }

    private static DiagnosticCollectorFailureKind MapFailureKind(
        OwnedProcessFailureKind kind)
    {
        return kind switch
        {
            OwnedProcessFailureKind.OperationDeadlineExceeded =>
                DiagnosticCollectorFailureKind.OperationDeadlineExceeded,
            OwnedProcessFailureKind.OwnedTreeNotQuiescent =>
                DiagnosticCollectorFailureKind.CollectorTreeNotQuiescent,
            OwnedProcessFailureKind.StreamDrainDeadlineExceeded =>
                DiagnosticCollectorFailureKind.StreamDrainDeadlineExceeded,
            OwnedProcessFailureKind.CallerCancelled =>
                DiagnosticCollectorFailureKind.CallerCancelled,
            _ => DiagnosticCollectorFailureKind.ExecutionFailed
        };
    }

    private static ReadOnlyCollection<DiagnosticCollectorCleanupFailure> MapCleanupFailures(
        IEnumerable<OwnedProcessCleanupStageFailure> failures)
    {
        return new ReadOnlyCollection<DiagnosticCollectorCleanupFailure>(
            failures.Select(MapCleanupFailure).ToArray());
    }

    private static DiagnosticCollectorCleanupFailure MapCleanupFailure(
        OwnedProcessCleanupStageFailure failure)
    {
        var kind = failure.Stage switch
        {
            OwnedProcessCleanupStage.Terminate =>
                DiagnosticCollectorCleanupFailureKind.TerminateFailed,
            OwnedProcessCleanupStage.TreeQuiescence =>
                DiagnosticCollectorCleanupFailureKind.CollectorTreeNotQuiescent,
            OwnedProcessCleanupStage.Reap when failure.Cause is TimeoutException =>
                DiagnosticCollectorCleanupFailureKind.ReapDeadlineExceeded,
            OwnedProcessCleanupStage.Reap =>
                DiagnosticCollectorCleanupFailureKind.ReapFailed,
            OwnedProcessCleanupStage.StreamDrain =>
                DiagnosticCollectorCleanupFailureKind.StreamDrainDeadlineExceeded,
            _ => DiagnosticCollectorCleanupFailureKind.DisposeFailed
        };
        return new DiagnosticCollectorCleanupFailure(kind, failure.Cause);
    }

    private static (
        Exception Primary,
        IReadOnlyList<DiagnosticCollectorCleanupFailure> CleanupFailures)
        SplitStartFailure(Exception failure)
    {
        if (failure is not AggregateException aggregate ||
            aggregate.InnerExceptions.Count == 0)
        {
            return (
                failure,
                Array.Empty<DiagnosticCollectorCleanupFailure>());
        }

        var primary = aggregate.InnerExceptions[0];
        var cleanup = aggregate.InnerExceptions
            .Skip(1)
            .Select(OwnedProcessCleanupStageFailure.FromException)
            .Select(MapCleanupFailure)
            .ToArray();
        return (
            primary,
            new ReadOnlyCollection<DiagnosticCollectorCleanupFailure>(cleanup));
    }

    private static DiagnosticCollectorEvidence CreateNotStartedEvidence(bool timedOut)
    {
        return new DiagnosticCollectorEvidence(
            Started: false,
            Exited: false,
            Reaped: false,
            StreamsDrained: false,
            TimedOut: timedOut,
            ExitCode: null,
            StandardOutput: string.Empty,
            StandardError: string.Empty);
    }

    private static DiagnosticCollectorExecutionException CreateFailure(
        DiagnosticCollectorFailureKind kind,
        DiagnosticCollectorEvidence evidence,
        Exception cause,
        IReadOnlyList<DiagnosticCollectorCleanupFailure> cleanupFailures)
    {
        return new DiagnosticCollectorExecutionException(
            new DiagnosticCollectorFailure(kind, evidence, cause),
            cleanupFailures);
    }
}
