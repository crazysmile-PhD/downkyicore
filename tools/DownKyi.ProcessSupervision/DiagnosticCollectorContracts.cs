using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace DownKyi.ProcessSupervision;

#pragma warning disable CA1515 // The executable supervisor intentionally exports collector contracts to PowerShell and platform tests.

public sealed class DiagnosticCollectorWindow
{
    private readonly TimeProvider _timeProvider;

    internal DiagnosticCollectorWindow(
        TransitionBudget parentBudget,
        TransitionBudget budget,
        TimeProvider timeProvider)
    {
        ParentBudget = parentBudget;
        Budget = budget;
        _timeProvider = timeProvider;
    }

    public TimeSpan RemainingOperation => Budget.RemainingOperation;

    public TimeSpan RemainingCleanup => Budget.RemainingCleanup;

    internal TransitionBudget Budget { get; }

    internal TransitionBudget ParentBudget { get; }

    public async Task DelayAsync(
        TimeSpan requestedDelay,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(requestedDelay, TimeSpan.Zero);
        if (requestedDelay == TimeSpan.Zero)
        {
            return;
        }

        var remaining = RemainingOperation;
        if (remaining <= TimeSpan.Zero)
        {
            throw new TimeoutException(
                "The diagnostic collector window operation deadline is exhausted.");
        }

        try
        {
            await Task.Delay(requestedDelay, _timeProvider, cancellationToken)
                .WaitAsync(remaining, _timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException failure)
        {
            throw new TimeoutException(
                "The diagnostic collector delay exceeded its caller-allocated window.",
                failure);
        }
    }
}

public sealed class DiagnosticCollectorRequest
{
    public DiagnosticCollectorRequest(
        LaunchSpec launch,
        DiagnosticCollectorWindow window)
    {
        Launch = launch ?? throw new ArgumentNullException(nameof(launch));
        Window = window ?? throw new ArgumentNullException(nameof(window));
    }

    public LaunchSpec Launch { get; }

    public DiagnosticCollectorWindow Window { get; }
}

public sealed record DiagnosticCollectorEvidence(
    bool Started,
    bool Exited,
    bool Reaped,
    bool StreamsDrained,
    bool TimedOut,
    int? ExitCode,
    string StandardOutput,
    string StandardError);

public sealed record DiagnosticCollectorOutcome(DiagnosticCollectorEvidence Evidence);

public enum DiagnosticCollectorFailureKind
{
    StartFailed,
    OperationDeadlineExceeded,
    CallerCancelled,
    CollectorTreeNotQuiescent,
    StreamDrainDeadlineExceeded,
    CleanupFailed,
    ExecutionFailed
}

public enum DiagnosticCollectorCleanupFailureKind
{
    TerminateFailed,
    CollectorTreeNotQuiescent,
    ReapDeadlineExceeded,
    ReapFailed,
    StreamDrainDeadlineExceeded,
    DisposeFailed
}

public sealed record DiagnosticCollectorCleanupFailure(
    DiagnosticCollectorCleanupFailureKind Kind,
    Exception Cause);

public sealed record DiagnosticCollectorFailure(
    DiagnosticCollectorFailureKind Kind,
    DiagnosticCollectorEvidence Evidence,
    Exception Cause);

[SuppressMessage(
    "Design",
    "CA1032:Implement standard exception constructors",
    Justification = "The collector boundary always requires typed primary and cleanup evidence.")]
public sealed class DiagnosticCollectorExecutionException : Exception
{
    internal DiagnosticCollectorExecutionException(
        DiagnosticCollectorFailure failure,
        IReadOnlyList<DiagnosticCollectorCleanupFailure> cleanupFailures)
        : base(CreateMessage(failure, cleanupFailures), failure.Cause)
    {
        Failure = failure;
        CleanupFailures = new ReadOnlyCollection<DiagnosticCollectorCleanupFailure>(
            cleanupFailures.ToArray());
    }

    public DiagnosticCollectorFailure Failure { get; }

    public IReadOnlyList<DiagnosticCollectorCleanupFailure> CleanupFailures { get; }

    private static string CreateMessage(
        DiagnosticCollectorFailure failure,
        IReadOnlyList<DiagnosticCollectorCleanupFailure> cleanupFailures)
    {
        return cleanupFailures.Count == 0
            ? $"Diagnostic collector execution failed: {failure.Kind}."
            : $"Diagnostic collector execution failed ({failure.Kind}) and cleanup " +
              $"reported {cleanupFailures.Count} failure(s).";
    }
}

[Flags]
internal enum DiagnosticCollectorMutation
{
    None = 0,
    IgnoreAllocatedWindow = 1,
    FailAfterTerminate = 2,
    StallReap = 4,
    StallStreamDrain = 8
}
