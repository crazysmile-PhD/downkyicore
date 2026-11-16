using System.Runtime.CompilerServices;
using System.Text.Json;
using DownKyi.Application.Bilibili;
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

    public static Task<T> RequestJsonAsync<T>(
        IBilibiliApiClient client,
        string url,
        string? referer,
        string operationName,
        string logTag,
        bool includeCredentials = true,
        CancellationToken cancellationToken = default)
    {
        return RequestJsonAsync<T>(
            client,
            url,
            referer,
            operationName,
            logTag,
            serializerSettings: null,
            includeCredentials,
            cancellationToken);
    }

    public static Task<T> RequestJsonAsync<T>(
        IBilibiliApiClient client,
        string url,
        string? referer,
        string operationName,
        string logTag,
        JsonSerializerSettings? serializerSettings,
        bool includeCredentials = true,
        CancellationToken cancellationToken = default)
    {
        return RequestJsonCoreAsync<T>(
            client,
            url,
            referer,
            operationName,
            logTag,
            serializerSettings,
            allowedNonSuccessCode: null,
            includeCredentials,
            cancellationToken);
    }

    public static Task<T> RequestJsonAllowingCodeAsync<T>(
        IBilibiliApiClient client,
        string url,
        string? referer,
        string operationName,
        string logTag,
        int allowedNonSuccessCode,
        bool includeCredentials = true,
        CancellationToken cancellationToken = default)
    {
        if (allowedNonSuccessCode == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(allowedNonSuccessCode),
                allowedNonSuccessCode,
                "The explicit exception must be a non-success API code.");
        }

        return RequestJsonCoreAsync<T>(
            client,
            url,
            referer,
            operationName,
            logTag,
            serializerSettings: null,
            allowedNonSuccessCode,
            includeCredentials,
            cancellationToken);
    }

    private static async Task<T> RequestJsonCoreAsync<T>(
        IBilibiliApiClient client,
        string url,
        string? referer,
        string operationName,
        string logTag,
        JsonSerializerSettings? serializerSettings,
        int? allowedNonSuccessCode,
        bool includeCredentials,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentException.ThrowIfNullOrWhiteSpace(logTag);
        var response = await client.GetStringAsync(
            new BilibiliHttpRequest(
                url,
                referer,
                includeCredentials,
                includeBuvid: includeCredentials),
            cancellationToken).ConfigureAwait(false);
        return ParseJson<T>(
            response,
            operationName,
            serializerSettings,
            allowedNonSuccessCode);
    }

    public static T ParseJson<T>(
        string response,
        string operationName,
        JsonSerializerSettings? serializerSettings = null,
        int? allowedNonSuccessCode = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
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

    public static Task<string> RequestTextAsync(
        IBilibiliApiClient client,
        string url,
        string? referer,
        string operationName,
        string logTag,
        bool includeCredentials = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentException.ThrowIfNullOrWhiteSpace(logTag);
        return client.GetStringAsync(
            new BilibiliHttpRequest(
                url,
                referer,
                includeCredentials,
                includeBuvid: includeCredentials),
            cancellationToken);
    }
}
