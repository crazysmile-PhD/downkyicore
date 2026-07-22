using System.Runtime.CompilerServices;
using System.Text.Json;
using Newtonsoft.Json;
using JsonException = Newtonsoft.Json.JsonException;

namespace DownKyi.Core.BiliApi;

public sealed class BilibiliApiResponseException : InvalidOperationException
{
    public BilibiliApiResponseException()
        : this("unknown", "A Bilibili API response could not be parsed.")
    {
    }

    public BilibiliApiResponseException(string message)
        : this("unknown", message)
    {
    }

    public BilibiliApiResponseException(string message, Exception innerException)
        : this("unknown", message, innerException)
    {
    }

    public BilibiliApiResponseException(
        string operation,
        string message,
        Exception? innerException = null,
        int? code = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        Operation = operation;
        Code = code;
    }

    public string Operation { get; }

    public int? Code { get; }
}

internal static class BiliApiRequest
{
    public static TPayload RequirePayload<TPayload>(
        TPayload? payload,
        string fieldName = "data",
        [CallerMemberName] string operationName = "unknown")
        where TPayload : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        return payload ?? throw new BilibiliApiResponseException(
            operationName,
            $"{operationName} returned a successful response without the required '{fieldName}' payload.");
    }

    public static T RequestJson<T>(
        string url,
        string? referer,
        string operationName,
        string logTag,
        CancellationToken cancellationToken = default)
    {
        return RequestJson<T>(
            url,
            referer,
            operationName,
            logTag,
            serializerSettings: null,
            cancellationToken);
    }

    public static T RequestJson<T>(
        string url,
        string? referer,
        string operationName,
        string logTag,
        JsonSerializerSettings? serializerSettings,
        CancellationToken cancellationToken = default)
    {
        return RequestJsonCore<T>(
            url,
            referer,
            operationName,
            logTag,
            serializerSettings,
            allowedNonSuccessCode: null,
            cancellationToken);
    }

    public static T RequestJsonAllowingCode<T>(
        string url,
        string? referer,
        string operationName,
        string logTag,
        int allowedNonSuccessCode,
        CancellationToken cancellationToken = default)
    {
        if (allowedNonSuccessCode == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(allowedNonSuccessCode),
                allowedNonSuccessCode,
                "The explicit exception must be a non-success API code.");
        }

        return RequestJsonCore<T>(
            url,
            referer,
            operationName,
            logTag,
            serializerSettings: null,
            allowedNonSuccessCode,
            cancellationToken);
    }

    private static T RequestJsonCore<T>(
        string url,
        string? referer,
        string operationName,
        string logTag,
        JsonSerializerSettings? serializerSettings,
        int? allowedNonSuccessCode,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentException.ThrowIfNullOrWhiteSpace(logTag);
        var response = WebClient.RequestWeb(url, referer, cancellationToken: cancellationToken);
        try
        {
            var metadata = System.Text.Json.JsonSerializer.Deserialize(
                response,
                BilibiliWebJsonContext.Default.BilibiliResponseMetadata);
            if (metadata?.Code is { } code and not 0
                && code != allowedNonSuccessCode)
            {
                throw new BilibiliApiResponseException(
                    operationName,
                    $"{operationName} was rejected by Bilibili. code={code}; message={metadata.Message ?? "unknown"}",
                    code: code);
            }

            var result = JsonConvert.DeserializeObject<T>(response, serializerSettings);
            return result is null
                ? throw new BilibiliApiResponseException(
                    operationName,
                    $"{operationName} returned an empty JSON value.")
                : result;
        }
        catch (System.Text.Json.JsonException e)
        {
            throw new BilibiliApiResponseException(
                operationName,
                $"{operationName} returned malformed JSON.",
                e);
        }
        catch (JsonException e)
        {
            throw new BilibiliApiResponseException(
                operationName,
                $"{operationName} returned an invalid JSON schema.",
                e);
        }
    }

    public static string RequestText(
        string url,
        string? referer,
        string operationName,
        string logTag,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentException.ThrowIfNullOrWhiteSpace(logTag);
        return WebClient.RequestWeb(url, referer, cancellationToken: cancellationToken);
    }
}
