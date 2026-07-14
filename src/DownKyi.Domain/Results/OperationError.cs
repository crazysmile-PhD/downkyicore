namespace DownKyi.Domain.Results;

public sealed record OperationError(
    string Code,
    string Message,
    OperationErrorKind Kind = OperationErrorKind.Unexpected)
{
    public static OperationError Unexpected(string code, string message)
    {
        return new OperationError(code, message);
    }
}
