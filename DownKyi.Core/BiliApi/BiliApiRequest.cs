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

    public BilibiliApiResponseException(string operation, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        Operation = operation;
    }

    public string Operation { get; }
}

internal static class BiliApiRequest
{
    public static T RequestJson<T>(
        string url,
        string? referer,
        string operationName,
        string logTag,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentException.ThrowIfNullOrWhiteSpace(logTag);
        var response = WebClient.RequestWeb(url, referer, cancellationToken: cancellationToken);
        try
        {
            var metadata = System.Text.Json.JsonSerializer.Deserialize(
                response,
                BilibiliWebJsonContext.Default.BilibiliResponseMetadata);
            if (metadata?.Code is { } code and not 0)
            {
                throw new BilibiliApiResponseException(
                    operationName,
                    $"{operationName} was rejected by Bilibili. code={code}; message={metadata.Message ?? "unknown"}");
            }

            var result = JsonConvert.DeserializeObject<T>(response);
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
