using System.Net;
using DownKyi.Core.BiliApi.Http;
using DownKyi.Core.Storage;
using Xunit;

namespace DownKyi.Core.Tests.BiliApi.Http;

public class BiliHttpClientTests
{
    [Fact]
    public async Task SendAsync_WhenFirstAttemptFailsAndSecondSucceeds_ReturnsSuccess()
    {
        var handler = new SequenceHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok")
            });
        var client = CreateClient(handler);

        var result = await client.SendAsync("https://example.test/api", retry: 2);

        Assert.True(result.IsSuccess);
        Assert.Equal("ok", result.Value);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task SendAsync_WhenRetryAttemptsAreExhausted_ReturnsFailureWithStatusCode()
    {
        var handler = new SequenceHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var client = CreateClient(handler);

        var result = await client.SendAsync("https://example.test/api", retry: 2);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Equal(HttpStatusCode.InternalServerError, result.StatusCode);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task SendAsync_EncodesQueryStringParameters()
    {
        Uri? requestedUri = null;
        var handler = new SequenceHttpMessageHandler(request =>
        {
            requestedUri = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok")
            };
        });
        var client = CreateClient(handler);
        var parameters = new Dictionary<string, object?>
        {
            ["中文"] = "測試",
            ["space"] = "a b",
            ["special"] = "&=?%"
        };

        var result = await client.SendAsync("https://example.test/api?existing=1", parameters: parameters);

        Assert.True(result.IsSuccess);
        Assert.NotNull(requestedUri);
        var absoluteUri = requestedUri!.AbsoluteUri;
        Assert.Contains("existing=1", absoluteUri);
        Assert.Contains("%E4%B8%AD%E6%96%87=%E6%B8%AC%E8%A9%A6", absoluteUri);
        Assert.Contains("space=a%20b", absoluteUri);
        Assert.Contains("special=%26%3D%3F%25", absoluteUri);
    }

    [Fact]
    public async Task SendAsync_WhenCancellationIsRequested_DoesNotContinueRetrying()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        var handler = new SequenceHttpMessageHandler(_ =>
        {
            cancellationTokenSource.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(cancellationTokenSource.Token);
        });
        var client = CreateClient(handler);

        var result = await client.SendAsync("https://example.test/api", retry: 3, cancellationToken: cancellationTokenSource.Token);

        Assert.False(result.IsSuccess);
        Assert.IsType<TaskCanceledException>(result.Exception);
        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task SendAsync_WhenHttpStatusIsNotSuccessful_PreservesStatusCode(HttpStatusCode statusCode)
    {
        var handler = new SequenceHttpMessageHandler(_ => new HttpResponseMessage(statusCode));
        var client = CreateClient(handler);

        var result = await client.SendAsync("https://example.test/api", retry: 1);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Equal(statusCode, result.StatusCode);
        Assert.Contains(((int)statusCode).ToString(), result.ErrorMessage);
    }

    [Fact]
    public async Task SendAsync_AddsConfiguredHeadersAndCookies()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new SequenceHttpMessageHandler(request =>
        {
            capturedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok")
            };
        });
        var client = CreateClient(handler, new FakeCookieProvider("SESSDATA=abc; buvid3=buvid"), new FakeUserAgentProvider("unit-test-agent"));

        var result = await client.SendAsync("https://example.test/api", referer: "https://www.bilibili.com/video/BV1xx411c7mQ");

        Assert.True(result.IsSuccess);
        Assert.NotNull(capturedRequest);
        Assert.Equal("https://www.bilibili.com/video/BV1xx411c7mQ", capturedRequest!.Headers.Referrer?.ToString());
        Assert.True(capturedRequest.Headers.TryGetValues("cookie", out var cookieValues));
        Assert.Contains("SESSDATA=abc; buvid3=buvid", cookieValues);
        Assert.True(capturedRequest.Headers.TryGetValues("User-Agent", out var userAgentValues));
        Assert.Contains("unit-test-agent", userAgentValues);
    }

    private static BiliHttpClient CreateClient(
        HttpMessageHandler handler,
        IBiliCookieProvider? cookieProvider = null,
        IUserAgentProvider? userAgentProvider = null)
    {
        return new BiliHttpClient(
            new HttpClient(handler),
            cookieProvider ?? new FakeCookieProvider(),
            userAgentProvider ?? new FakeUserAgentProvider());
    }

    private class SequenceHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _responses;

        public int RequestCount { get; private set; }

        public SequenceHttpMessageHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
        {
            _responses = new Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>>(
                responses.Select<Func<HttpRequestMessage, HttpResponseMessage>, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>>(response =>
                    (request, _) => Task.FromResult(response(request))));
        }

        public SequenceHttpMessageHandler(params Func<HttpRequestMessage, Task<HttpResponseMessage>>[] responses)
        {
            _responses = new Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>>(
                responses.Select<Func<HttpRequestMessage, Task<HttpResponseMessage>>, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>>(response =>
                    (request, _) => response(request)));
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return _responses.Count > 1
                ? _responses.Dequeue()(request, cancellationToken)
                : _responses.Peek()(request, cancellationToken);
        }
    }

    private class FakeCookieProvider : IBiliCookieProvider
    {
        private readonly string _cookieHeader;

        public FakeCookieProvider(string cookieHeader = "")
        {
            _cookieHeader = cookieHeader;
        }

        public Task<IReadOnlyList<DownKyiCookie>> GetCookiesAsync(bool includeBuvid, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<DownKyiCookie>>(Array.Empty<DownKyiCookie>());
        }

        public Task<string> GetCookieHeaderAsync(bool includeBuvid, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_cookieHeader);
        }
    }

    private class FakeUserAgentProvider : IUserAgentProvider
    {
        private readonly string _userAgent;

        public FakeUserAgentProvider(string userAgent = "unit-test")
        {
            _userAgent = userAgent;
        }

        public string GetUserAgent()
        {
            return _userAgent;
        }
    }
}
