using System.Collections.ObjectModel;

namespace DownKyi.ProcessSupervision;

internal abstract class ProcessContainmentPrimaryFailure
{
    private protected ProcessContainmentPrimaryFailure(
        string errorType,
        string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorType);
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        ErrorType = errorType;
        Detail = detail;
    }

    public string ErrorType { get; }

    public string Detail { get; }
}

internal abstract class ProcessContainmentBackendFailure
    : ProcessContainmentPrimaryFailure
{
    private protected ProcessContainmentBackendFailure(
        object authorityIdentity,
        string errorType,
        string detail)
        : base(errorType, detail)
    {
        ArgumentNullException.ThrowIfNull(authorityIdentity);
        AuthorityIdentity = authorityIdentity;
    }

    internal object AuthorityIdentity { get; }
}

internal enum ProcessContainmentCallerFailureKind
{
    Cancellation,
    DeadlineExceeded
}

internal sealed class ProcessContainmentCallerFailure
    : ProcessContainmentPrimaryFailure
{
    private readonly object _authorityIdentity;

    private ProcessContainmentCallerFailure(
        object authorityIdentity,
        ProcessContainmentCallerFailureKind kind,
        string detail)
        : base(ErrorTypeFor(kind), detail)
    {
        ArgumentNullException.ThrowIfNull(authorityIdentity);
        _authorityIdentity = authorityIdentity;
        Kind = kind;
    }

    public ProcessContainmentCallerFailureKind Kind { get; }

    private bool IsOwnedBy(object authorityIdentity)
    {
        return ReferenceEquals(_authorityIdentity, authorityIdentity);
    }

    private static string ErrorTypeFor(ProcessContainmentCallerFailureKind kind)
    {
        return kind switch
        {
            ProcessContainmentCallerFailureKind.Cancellation =>
                nameof(OperationCanceledException),
            ProcessContainmentCallerFailureKind.DeadlineExceeded =>
                nameof(TimeoutException),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
    }

    internal sealed class Publisher
    {
        private readonly object _authorityIdentity = new();

        internal ProcessContainmentCallerFailure PublishCancellation(
            string detail)
        {
            return new ProcessContainmentCallerFailure(
                _authorityIdentity,
                ProcessContainmentCallerFailureKind.Cancellation,
                detail);
        }

        internal ProcessContainmentCallerFailure PublishDeadlineExceeded(
            string detail)
        {
            return new ProcessContainmentCallerFailure(
                _authorityIdentity,
                ProcessContainmentCallerFailureKind.DeadlineExceeded,
                detail);
        }

        internal bool Owns(ProcessContainmentCallerFailure failure)
        {
            ArgumentNullException.ThrowIfNull(failure);
            return failure.IsOwnedBy(_authorityIdentity);
        }
    }
}

internal abstract class ProcessContainmentContractFailure
    : ProcessContainmentPrimaryFailure
{
    private protected ProcessContainmentContractFailure(
        object authorityIdentity,
        string errorType,
        string detail)
        : base(errorType, detail)
    {
        ArgumentNullException.ThrowIfNull(authorityIdentity);
        AuthorityIdentity = authorityIdentity;
    }

    internal object AuthorityIdentity { get; }
}

internal sealed class ProcessContainmentCallerAuthority
{
    private readonly ProcessContainmentCallerFailure.Publisher _publisher = new();

    internal ProcessContainmentCallerAuthority(
        TransitionBudget budget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(budget);
        Budget = budget;
        CancellationToken = cancellationToken;
    }

    public TransitionBudget Budget { get; }

    public CancellationToken CancellationToken { get; }

    internal ProcessContainmentCallerFailure PublishCancellation(string detail)
    {
        if (!CancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                "Caller cancellation cannot be published before its bound capability is canceled.");
        }

        return _publisher.PublishCancellation(detail);
    }

    internal ProcessContainmentCallerFailure PublishDeadlineExceeded(string detail)
    {
        if (!Budget.Operation.IsExpired)
        {
            throw new InvalidOperationException(
                "Caller deadline cannot be published before the bound root budget observes expiry.");
        }

        return _publisher.PublishDeadlineExceeded(detail);
    }

    internal bool Owns(ProcessContainmentCallerFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return _publisher.Owns(failure);
    }
}

internal abstract record ProcessContainmentBackendResult
{
    private protected ProcessContainmentBackendResult(object authorityIdentity)
    {
        ArgumentNullException.ThrowIfNull(authorityIdentity);
        AuthorityIdentity = authorityIdentity;
    }

    internal object AuthorityIdentity { get; }
}

internal sealed class ProcessContainmentBackendResultFactory
{
    private readonly object _authorityIdentity = new();

    internal ProcessContainmentBackendResultFactory()
    {
    }

    internal ProcessContainmentBackendResult Succeeded(string evidence)
    {
        return new PublishedBackendSucceeded(_authorityIdentity, evidence);
    }

    internal ProcessContainmentBackendResult Failed(
        Exception failure,
        string detail)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new PublishedBackendFailed(
            _authorityIdentity,
            new PublishedBackendOperationFailure(
                _authorityIdentity,
                failure.GetType().Name,
                detail));
    }

    internal bool Owns(ProcessContainmentBackendResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return ReferenceEquals(_authorityIdentity, result.AuthorityIdentity);
    }

    internal bool Owns(ProcessContainmentBackendFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return ReferenceEquals(_authorityIdentity, failure.AuthorityIdentity);
    }
}

internal sealed class ProcessContainmentContractGuard
{
    private readonly object _authorityIdentity = new();

    internal ProcessContainmentContractGuard()
    {
    }

    internal ProcessContainmentContractFailure IllegalTransition(string detail)
    {
        return new PublishedIllegalTransitionFailure(
            _authorityIdentity,
            detail);
    }

    internal ProcessContainmentContractFailure InvalidBackendResult(string detail)
    {
        return new PublishedInvalidBackendResultFailure(
            _authorityIdentity,
            detail);
    }

    internal ProcessContainmentContractFailure AuthoritySubstitution()
    {
        return new PublishedAuthoritySubstitutionFailure(_authorityIdentity);
    }

    internal bool Owns(ProcessContainmentContractFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return ReferenceEquals(_authorityIdentity, failure.AuthorityIdentity);
    }
}

internal abstract record ProcessContainmentOperationResult
{
    private protected ProcessContainmentOperationResult(
        IEnumerable<ProcessCleanupFailure> cleanupFailures)
    {
        ArgumentNullException.ThrowIfNull(cleanupFailures);
        var snapshot = cleanupFailures.ToArray();
        if (snapshot.Any(failure => failure is null))
        {
            throw new ArgumentException(
                "Cleanup failures cannot contain a null item.",
                nameof(cleanupFailures));
        }

        CleanupFailures = new ReadOnlyCollection<ProcessCleanupFailure>(snapshot);
    }

    public IReadOnlyList<ProcessCleanupFailure> CleanupFailures { get; }
}

internal abstract record ProcessContainmentOperationCompleted
    : ProcessContainmentOperationResult
{
    private protected ProcessContainmentOperationCompleted(
        string evidence,
        IEnumerable<ProcessCleanupFailure> cleanupFailures)
        : base(cleanupFailures)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence);
        Evidence = evidence;
    }

    public string Evidence { get; }
}

internal abstract record ProcessContainmentOperationRejected
    : ProcessContainmentOperationResult
{
    private protected ProcessContainmentOperationRejected(
        ProcessContainmentPrimaryFailure primaryFailure,
        IEnumerable<ProcessCleanupFailure> cleanupFailures)
        : base(cleanupFailures)
    {
        ArgumentNullException.ThrowIfNull(primaryFailure);
        PrimaryFailure = primaryFailure;
    }

    public ProcessContainmentPrimaryFailure PrimaryFailure { get; }
}

internal sealed class ProcessContainmentOperationAuthority
{
    private ProcessContainmentOperationAuthority(
        TransitionBudget budget,
        CancellationToken cancellationToken)
    {
        Caller = new ProcessContainmentCallerAuthority(
            budget,
            cancellationToken);
        BackendResults = new ProcessContainmentBackendResultFactory();
        ContractGuard = new ProcessContainmentContractGuard();
    }

    public ProcessContainmentCallerAuthority Caller { get; }

    public ProcessContainmentBackendResultFactory BackendResults { get; }

    public ProcessContainmentContractGuard ContractGuard { get; }

    internal static ProcessContainmentOperationAuthority Create(
        TransitionBudget budget,
        CancellationToken cancellationToken)
    {
        return new ProcessContainmentOperationAuthority(
            budget,
            cancellationToken);
    }

    internal ProcessContainmentOperationResult FromBackend(
        ProcessContainmentBackendResult backendResult,
        IEnumerable<ProcessCleanupFailure> cleanupFailures)
    {
        ArgumentNullException.ThrowIfNull(backendResult);
        ArgumentNullException.ThrowIfNull(cleanupFailures);
        if (!BackendResults.Owns(backendResult))
        {
            return new PublishedOperationRejected(
                ContractGuard.AuthoritySubstitution(),
                cleanupFailures);
        }

        return backendResult switch
        {
            PublishedBackendSucceeded succeeded =>
                new PublishedOperationCompleted(
                    succeeded.Evidence,
                    cleanupFailures),
            PublishedBackendFailed failed when BackendResults.Owns(failed.Failure) =>
                new PublishedOperationRejected(
                    failed.Failure,
                    cleanupFailures),
            _ => new PublishedOperationRejected(
                ContractGuard.InvalidBackendResult(
                    "The backend returned an unsupported result type."),
                cleanupFailures)
        };
    }

    internal ProcessContainmentOperationResult Rejected(
        ProcessContainmentCallerFailure primaryFailure,
        IEnumerable<ProcessCleanupFailure> cleanupFailures)
    {
        ArgumentNullException.ThrowIfNull(primaryFailure);
        return Caller.Owns(primaryFailure)
            ? new PublishedOperationRejected(primaryFailure, cleanupFailures)
            : new PublishedOperationRejected(
                ContractGuard.AuthoritySubstitution(),
                cleanupFailures);
    }

    internal ProcessContainmentOperationResult Rejected(
        ProcessContainmentContractFailure primaryFailure,
        IEnumerable<ProcessCleanupFailure> cleanupFailures)
    {
        ArgumentNullException.ThrowIfNull(primaryFailure);
        return ContractGuard.Owns(primaryFailure)
            ? new PublishedOperationRejected(primaryFailure, cleanupFailures)
            : new PublishedOperationRejected(
                ContractGuard.AuthoritySubstitution(),
                cleanupFailures);
    }
}

file sealed class PublishedBackendOperationFailure
    : ProcessContainmentBackendFailure
{
    internal PublishedBackendOperationFailure(
        object authorityIdentity,
        string errorType,
        string detail)
        : base(authorityIdentity, errorType, detail)
    {
    }
}

file sealed class PublishedIllegalTransitionFailure
    : ProcessContainmentContractFailure
{
    internal PublishedIllegalTransitionFailure(
        object authorityIdentity,
        string detail)
        : base(authorityIdentity, nameof(InvalidOperationException), detail)
    {
    }
}

file sealed class PublishedInvalidBackendResultFailure
    : ProcessContainmentContractFailure
{
    internal PublishedInvalidBackendResultFailure(
        object authorityIdentity,
        string detail)
        : base(authorityIdentity, nameof(InvalidOperationException), detail)
    {
    }
}

file sealed class PublishedAuthoritySubstitutionFailure
    : ProcessContainmentContractFailure
{
    internal PublishedAuthoritySubstitutionFailure(object authorityIdentity)
        : base(
            authorityIdentity,
            nameof(InvalidOperationException),
            "The candidate authority does not own this operation lifetime.")
    {
    }
}

file sealed record PublishedBackendSucceeded : ProcessContainmentBackendResult
{
    internal PublishedBackendSucceeded(object authorityIdentity, string evidence)
        : base(authorityIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence);
        Evidence = evidence;
    }

    public string Evidence { get; }
}

file sealed record PublishedBackendFailed : ProcessContainmentBackendResult
{
    internal PublishedBackendFailed(
        object authorityIdentity,
        ProcessContainmentBackendFailure failure)
        : base(authorityIdentity)
    {
        ArgumentNullException.ThrowIfNull(failure);
        Failure = failure;
    }

    public ProcessContainmentBackendFailure Failure { get; }
}

file sealed record PublishedOperationCompleted
    : ProcessContainmentOperationCompleted
{
    internal PublishedOperationCompleted(
        string evidence,
        IEnumerable<ProcessCleanupFailure> cleanupFailures)
        : base(evidence, cleanupFailures)
    {
    }
}

file sealed record PublishedOperationRejected
    : ProcessContainmentOperationRejected
{
    internal PublishedOperationRejected(
        ProcessContainmentPrimaryFailure primaryFailure,
        IEnumerable<ProcessCleanupFailure> cleanupFailures)
        : base(primaryFailure, cleanupFailures)
    {
    }
}
