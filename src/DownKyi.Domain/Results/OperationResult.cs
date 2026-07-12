using System.Diagnostics.CodeAnalysis;

namespace DownKyi.Domain.Results;

public sealed class OperationResult
{
    private OperationResult(OperationError? error)
    {
        Error = error;
    }

    public bool IsSuccess => Error is null;

    public OperationError? Error { get; }

    public static OperationResult Success()
    {
        return new OperationResult(null);
    }

    public static OperationResult Failure(OperationError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new OperationResult(error);
    }

    public static OperationResult<T> Success<T>(T value) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(value);
        return new OperationResult<T>(value);
    }

    public static OperationResult<T> Failure<T>(OperationError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new OperationResult<T>(error);
    }
}

public sealed class OperationResult<T>
{
    private readonly T? _value;

    internal OperationResult(T value)
    {
        _value = value;
    }

    internal OperationResult(OperationError error)
    {
        Error = error;
    }

    public bool IsSuccess => Error is null;

    public OperationError? Error { get; }

    public bool TryGetValue([MaybeNullWhen(false)] out T value)
    {
        value = _value;
        return IsSuccess;
    }

    public T RequireValue()
    {
        return IsSuccess
            ? _value!
            : throw new InvalidOperationException("A failed result has no value.");
    }
}
