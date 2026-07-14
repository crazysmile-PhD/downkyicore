namespace DownKyi.Domain.Results;

public enum OperationErrorKind
{
    Unexpected,
    Validation,
    NotFound,
    Conflict,
    Unauthorized,
    Forbidden,
    RateLimited,
    Network,
    Timeout,
    ExternalProtocol
}
