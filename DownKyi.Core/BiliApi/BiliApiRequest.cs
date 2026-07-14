using DownKyi.Core.Logging;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi;

internal static class BiliApiRequest
{
    public static T? RequestJson<T>(
        string url,
        string? referer,
        string operationName,
        string logTag,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = WebClient.RequestWeb(url, referer, cancellationToken: cancellationToken);
            return JsonConvert.DeserializeObject<T>(response);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException e)
        {
            LogManager.Error(logTag, e);
            return default;
        }
        catch (JsonException e)
        {
            LogManager.Error(logTag, e);
            return default;
        }
    }

    public static string? RequestText(
        string url,
        string? referer,
        string operationName,
        string logTag,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return WebClient.RequestWeb(url, referer, cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException e)
        {
            LogManager.Error(logTag, e);
            return null;
        }
    }
}
