using System.Collections.ObjectModel;

namespace DownKyi.ProcessSupervision;

internal abstract record ProcessContainmentPrimaryFailure
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

internal abstract record ProcessContainmentBackendFailure
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

internal abstract record ProcessContainmentCallerFailure
    : ProcessContainmentPrimaryFailure
{
    private protected ProcessContainmentCallerFailure(
        ProcessContainmentOperationAuthorityIdentity authorityIdentity,
        string errorType,
        string detail)
        : base(errorType, detail)
    {
        ArgumentNullException.ThrowIfNull(authorityIdentity);
        AuthorityIdentity = authorityIdentity;
    }

    public ProcessContainmentOperationAuthorityIdentity AuthorityIdentity { get; }
}

internal abstract record ProcessContainmentContractFailure
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

internal sealed class ProcessContainmentOperationAuthorityIdentity
{
    internal ProcessContainmentOperationAuthorityIdentity()
    {
    }
}

internal sealed class ProcessContainmentCallerAuthority
{
    internal ProcessContainmentCallerAuthority(
        TransitionBudget budget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(budget);
        Identity = new ProcessContainmentOperationAuthorityIdentity();
        Budget = budget;
        CancellationToken = cancellationToken;
    }

    public ProcessContainmentOperationAuthorityIdentity Identity { get; }

    public TransitionBudget Budget { get; }

    public CancellationToken CancellationToken { get; }

    internal ProcessContainmentCallerFailure PublishCancellation(string detail)
    {
        if (!CancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                "Caller cancellation cannot be published before its bound capability is canceled.");
        }

        return new PublishedCallerCancellationFailure(Identity, detail);
    }

    internal ProcessContainmentCallerFailure PublishDeadlineExceeded(string detail)
    {
        if (!Budget.Operation.IsExpired)
        {
            throw new InvalidOperationException(
                "Caller deadline cannot be published before the bound root budget observes expiry.");
        }

        return new PublishedCallerDeadlineExceededFailure(Identity, detail);
    }

    internal bool Owns(ProcessContainmentCallerFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return ReferenceEquals(Identity, failure.AuthorityIdentity);
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

internal abstract record ProcessContainmentOperationResult;

internal abstract record ProcessContainmentOperationCompleted
    : ProcessContainmentOperationResult
{
    private protected ProcessContainmentOperationCompleted(string evidence)
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
    {
        ArgumentNullException.ThrowIfNull(primaryFailure);
        ArgumentNullException.ThrowIfNull(cleanupFailures);
        var snapshot = cleanupFailures.ToArray();
        if (snapshot.Any(failure => failure is null))
        {
            throw new ArgumentException(
                "Cleanup failures cannot contain a null item.",
                nameof(cleanupFailures));
        }

        PrimaryFailure = primaryFailure;
        CleanupFailures = new ReadOnlyCollection<ProcessCleanupFailure>(snapshot);
    }

    public ProcessContainmentPrimaryFailure PrimaryFailure { get; }

    public IReadOnlyList<ProcessCleanupFailure> CleanupFailures { get; }
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

    public ProcessContainmentOperationAuthorityIdentity Identity => Caller.Identity;

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
        ProcessContainmentBackendResult backendResult)
    {
        ArgumentNullException.ThrowIfNull(backendResult);
        if (!BackendResults.Owns(backendResult))
        {
            return new PublishedOperationRejected(
                ContractGuard.AuthoritySubstitution(),
                []);
        }

        return backendResult switch
        {
            PublishedBackendSucceeded succeeded =>
                new PublishedOperationCompleted(succeeded.Evidence),
            PublishedBackendFailed failed when BackendResults.Owns(failed.Failure) =>
                new PublishedOperationRejected(failed.Failure, []),
            _ => new PublishedOperationRejected(
                ContractGuard.InvalidBackendResult(
                    "The backend returned an unsupported result type."),
                [])
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

file sealed record PublishedBackendOperationFailure
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

file sealed record PublishedCallerCancellationFailure
    : ProcessContainmentCallerFailure
{
    internal PublishedCallerCancellationFailure(
        ProcessContainmentOperationAuthorityIdentity authorityIdentity,
        string detail)
        : base(
            authorityIdentity,
            nameof(OperationCanceledException),
            detail)
    {
    }
}

file sealed record PublishedCallerDeadlineExceededFailure
    : ProcessContainmentCallerFailure
{
    internal PublishedCallerDeadlineExceededFailure(
        ProcessContainmentOperationAuthorityIdentity authorityIdentity,
        string detail)
        : base(authorityIdentity, nameof(TimeoutException), detail)
    {
    }
}

file sealed record PublishedIllegalTransitionFailure
    : ProcessContainmentContractFailure
{
    internal PublishedIllegalTransitionFailure(
        object authorityIdentity,
        string detail)
        : base(authorityIdentity, nameof(InvalidOperationException), detail)
    {
    }
}

file sealed record PublishedInvalidBackendResultFailure
    : ProcessContainmentContractFailure
{
    internal PublishedInvalidBackendResultFailure(
        object authorityIdentity,
        string detail)
        : base(authorityIdentity, nameof(InvalidOperationException), detail)
    {
    }
}

file sealed record PublishedAuthoritySubstitutionFailure
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
    internal PublishedOperationCompleted(string evidence)
        : base(evidence)
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
