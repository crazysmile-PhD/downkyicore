using System.Text;
using DownKyi.Core.Aria2cNet.Client.Entity;
using Newtonsoft.Json;

namespace DownKyi.Core.Aria2cNet.Client;

/// <summary>
/// http://aria2.github.io/manual/en/html/aria2c.html#methods
/// </summary>
public sealed partial class AriaClient
{
    private const string JSONRPC = "2.0";
    private static readonly HttpClient HttpClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false
    });
    private readonly Func<Uri, string, CancellationToken, Task<string?>> _requestAsync;
    private readonly Uri _rpcUri;
    private readonly string _token;

    public AriaClient(
        string host,
        int listenPort,
        string token)
        : this(
            host,
            listenPort,
            token,
            static (url, parameters, cancellationToken) =>
                RequestAsync(url, parameters, cancellationToken))
    {
    }

    internal AriaClient(
        string host,
        int listenPort,
        string token,
        Func<Uri, string, Task<string?>> requestAsync)
        : this(
            host,
            listenPort,
            token,
            (url, parameters, _) => requestAsync(url, parameters))
    {
        ArgumentNullException.ThrowIfNull(requestAsync);
    }

    internal AriaClient(
        string host,
        int listenPort,
        string token,
        Func<Uri, string, CancellationToken, Task<string?>> requestAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(listenPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(listenPort, 65535);
        if (!Uri.TryCreate(host, UriKind.Absolute, out var hostUri)
            || (!string.Equals(hostUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(hostUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("The aria2 RPC host must be an absolute HTTP or HTTPS URI.", nameof(host));
        }

        if (string.Equals(hostUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !hostUri.IsLoopback)
        {
            throw new ArgumentException(
                "Remote aria2 RPC endpoints must use HTTPS.",
                nameof(host));
        }

        if (!string.IsNullOrEmpty(hostUri.UserInfo))
        {
            throw new ArgumentException(
                "The aria2 RPC host must not contain user information.",
                nameof(host));
        }

        _rpcUri = new UriBuilder(hostUri)
        {
            Port = listenPort,
            Path = "jsonrpc",
            Query = string.Empty,
            Fragment = string.Empty
        }.Uri;
        ArgumentNullException.ThrowIfNull(token);
        if (token.Length > 0 && string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException(
                "The aria2 RPC secret must be empty or contain a non-whitespace value.",
                nameof(token));
        }

        if (token.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The aria2 RPC secret contains a control character.",
                nameof(token));
        }

        _token = token;
        _requestAsync = requestAsync ?? throw new ArgumentNullException(nameof(requestAsync));
    }

    /// <summary>
    /// 发送http请求，并将返回的json反序列化
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="ariaSend"></param>
    /// <returns></returns>
    private async Task<T> GetRpcResponseAsync<T>(
        AriaSendData ariaSend,
        CancellationToken cancellationToken = default)
        where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_token.Length == 0
            && ariaSend.Params.Count > 0
            && ariaSend.Params[0] is string firstParameter
            && string.Equals(firstParameter, "token:", StringComparison.Ordinal))
        {
            ariaSend.Params = ariaSend.Params.Skip(1).ToArray();
        }

        // 去掉null
        var jsonSetting = new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore };
        // 转换为json字符串
        string sendJson = JsonConvert.SerializeObject(ariaSend, Formatting.Indented, jsonSetting);
        // 向服务器请求数据
        var result = await _requestAsync(
            _rpcUri,
            sendJson,
            cancellationToken).ConfigureAwait(false);
        if (result == null)
        {
            throw new HttpRequestException("aria2 RPC retry attempts were exhausted.");
        }

        return DeserializeRpcResponse<T>(result);
    }

    internal static T DeserializeRpcResponse<T>(string response)
        where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(response);
        var aria = JsonConvert.DeserializeObject<T>(response);
        return aria ?? throw new JsonSerializationException("aria2 RPC returned an empty JSON value.");
    }

    /// <summary>
    /// http请求
    /// </summary>
    /// <param name="url"></param>
    /// <param name="parameters"></param>
    /// <param name="retry"></param>
    /// <returns></returns>
    private static async Task<string?> RequestAsync(
        Uri url,
        string parameters,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(parameters, Encoding.UTF8, "application/json")
        };
        using var response = await HttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content
            .ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
    }

}
