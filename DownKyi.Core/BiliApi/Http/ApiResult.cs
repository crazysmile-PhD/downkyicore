using System.Net;

namespace DownKyi.Core.BiliApi.Http;

public class ApiResult<T>
{
    public bool IsSuccess { get; init; }

    public T? Value { get; init; }

    public HttpStatusCode? StatusCode { get; init; }

    public string? ErrorMessage { get; init; }

    public Exception? Exception { get; init; }

    public static ApiResult<T> Success(T value, HttpStatusCode? statusCode = null)
    {
        return new ApiResult<T>
        {
            IsSuccess = true,
            Value = value,
            StatusCode = statusCode
        };
    }

    public static ApiResult<T> Failure(string errorMessage, HttpStatusCode? statusCode = null, Exception? exception = null)
    {
        return new ApiResult<T>
        {
            IsSuccess = false,
            StatusCode = statusCode,
            ErrorMessage = errorMessage,
            Exception = exception
        };
    }
}
