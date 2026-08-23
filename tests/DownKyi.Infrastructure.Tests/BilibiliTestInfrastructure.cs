using System.Net;

namespace DownKyi.Infrastructure.Tests;

internal sealed class DelegateHttpMessageHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync)
    : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        return sendAsync(request, cancellationToken);
    }
}

internal sealed class TestHttpClientFactory : IHttpClientFactory, IDisposable
{
    private readonly HttpMessageHandler _handler;
    private readonly HttpClient _client;

    public TestHttpClientFactory(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync)
    {
        ArgumentNullException.ThrowIfNull(sendAsync);
        _handler = new DelegateHttpMessageHandler(sendAsync);
        _client = new HttpClient(_handler, disposeHandler: false);
    }

    private TestHttpClientFactory(bool useProxy)
    {
        _handler = new SocketsHttpHandler { UseProxy = useProxy };
        _client = new HttpClient(_handler, disposeHandler: false);
    }

    public static TestHttpClientFactory CreateSockets(bool useProxy) => new(useProxy);

    public HttpClient CreateClient(string name)
    {
        return _client;
    }

    public void Dispose()
    {
        _client.Dispose();
        _handler.Dispose();
    }
}

internal sealed class EmptyCookieProvider : Application.Bilibili.IBilibiliCookieProvider
{
    public string GetCookieHeader() => string.Empty;
}

internal sealed class ThrowingCookieProvider : Application.Bilibili.IBilibiliCookieProvider
{
    public string GetCookieHeader()
    {
        throw new InvalidOperationException("Credentials were not expected.");
    }
}

internal sealed class StubBuvidProvider : Application.Bilibili.IBuvidProvider
{
    public int Calls { get; private set; }

    public Task<Application.Bilibili.BilibiliBuvid> GetAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        return Task.FromResult(new Application.Bilibili.BilibiliBuvid("synthetic-3", "synthetic-4"));
    }
}

internal static class BilibiliTestResponses
{
    public static HttpResponseMessage Json(
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string body = "{}")
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body)
        };
    }

    public static Task<HttpResponseMessage> CompletedJson(
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string body = "{}")
    {
        return Task.FromResult(Json(statusCode, body));
    }

    public static Task<HttpResponseMessage> CompletedRateLimit(TimeSpan retryAfter)
    {
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{}"),
            Headers =
            {
                RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter)
            }
        });
    }

    public static Task<HttpResponseMessage> CompletedJsonWithCookies(
        string body,
        params string[] setCookieHeaders)
    {
        var response = Json(body: body);
        response.Headers.TryAddWithoutValidation("Set-Cookie", setCookieHeaders);
        return Task.FromResult(response);
    }
}
