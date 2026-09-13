using System.Net;
using DownKyi.Application.Bilibili;
using DownKyi.Infrastructure.Bilibili;

namespace DownKyi.Infrastructure.Tests;

public sealed class BilibiliHttpTransportTests
{
    [Fact]
    public async Task TextResponsePreservesAllSetCookieHeaders()
    {
        using var factory = CreateFactory((_, _) =>
            BilibiliTestResponses.CompletedJsonWithCookies(
                """{"code":0}""",
                "SESSDATA=fixture-session; Domain=.bilibili.com; Path=/; Secure",
                "bili_jct=fixture-csrf; Domain=.bilibili.com; Path=/; Secure"));
        var transport = CreateTransport(factory);

        var response = await transport.GetResponseAsync(
            CreateRequest,
            1,
            requireContent: true,
            allowRedirectStatus: false,
            TestContext.Current.CancellationToken);

        Assert.Equal("""{"code":0}""", response.Content);
        Assert.Equal(2, response.SetCookieHeaders.Count);
        Assert.Contains(response.SetCookieHeaders, value => value.StartsWith("SESSDATA=", StringComparison.Ordinal));
        Assert.Contains(response.SetCookieHeaders, value => value.StartsWith("bili_jct=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AuthenticationFailureIsNotRetried()
    {
        var calls = 0;
        using var factory = CreateFactory((_, _) =>
        {
            calls++;
            return BilibiliTestResponses.CompletedJson(HttpStatusCode.Forbidden);
        });
        var transport = CreateTransport(factory);

        var exception = await Assert.ThrowsAsync<BilibiliHttpRequestException>(
            () => transport.GetStringAsync(CreateRequest, 3, TestContext.Current.CancellationToken));

        Assert.Equal(1, calls);
        Assert.Equal(BilibiliHttpFailureKind.Authentication, exception.FailureKind);
    }

    [Fact]
    public async Task RateLimitHonorsRetryAfterBeforeRetrying()
    {
        var calls = 0;
        var delays = new List<TimeSpan>();
        using var factory = CreateFactory((_, _) =>
        {
            calls++;
            if (calls == 1)
            {
                return BilibiliTestResponses.CompletedRateLimit(TimeSpan.FromSeconds(7));
            }

            return BilibiliTestResponses.CompletedJson();
        });
        var transport = CreateTransport(
            factory,
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        var content = await transport.GetStringAsync(
            CreateRequest,
            2,
            TestContext.Current.CancellationToken);

        Assert.Equal("{}", content);
        Assert.Equal(2, calls);
        Assert.Equal(TimeSpan.FromSeconds(7), Assert.Single(delays));
    }

    [Fact]
    public async Task ServerFailureAndEmptyBodyAreRetried()
    {
        var calls = 0;
        using var factory = CreateFactory((_, _) => ++calls switch
        {
            1 => BilibiliTestResponses.CompletedJson(HttpStatusCode.InternalServerError),
            2 => BilibiliTestResponses.CompletedJson(body: string.Empty),
            _ => BilibiliTestResponses.CompletedJson(body: """{"code":0}""")
        });
        var transport = CreateTransport(factory);

        var content = await transport.GetStringAsync(
            CreateRequest,
            3,
            TestContext.Current.CancellationToken);

        Assert.Equal("""{"code":0}""", content);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task CancellationDuringBackoffStopsFurtherAttempts()
    {
        var calls = 0;
        using var cancellation = new CancellationTokenSource();
        using var factory = CreateFactory((_, _) =>
        {
            calls++;
            return BilibiliTestResponses.CompletedJson(HttpStatusCode.InternalServerError);
        });
        var transport = CreateTransport(
            factory,
            async (_, token) =>
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transport.GetStringAsync(CreateRequest, 3, cancellation.Token));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task PreCanceledRequestDoesNotReachHandler()
    {
        var calls = 0;
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        using var factory = CreateFactory((_, _) =>
        {
            calls++;
            return BilibiliTestResponses.CompletedJson();
        });
        var transport = CreateTransport(factory);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transport.GetStringAsync(CreateRequest, 3, cancellation.Token));

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task DisposingReturnedStreamIsIdempotent()
    {
        var stream = new TrackingMemoryStream([1, 2, 3]);
        try
        {
            using var factory = CreateFactory((_, _) =>
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(stream)
                });
            });
            var transport = CreateTransport(factory);

            var responseStream = await transport.OpenReadAsync(
                CreateRequest,
                1,
                TestContext.Current.CancellationToken);
            await responseStream.DisposeAsync();
            var firstDisposeCalls = stream.DisposeCalls;
            await responseStream.DisposeAsync();

            Assert.True(stream.IsDisposed);
            Assert.True(firstDisposeCalls > 0);
            Assert.Equal(firstDisposeCalls, stream.DisposeCalls);
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(true);
        }
    }

    private static HttpRequestMessage CreateRequest()
    {
        return new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/api");
    }

    private static TestHttpClientFactory CreateFactory(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync)
    {
        return new TestHttpClientFactory(sendAsync);
    }

    private static BilibiliHttpTransport CreateTransport(
        IHttpClientFactory factory,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        return new BilibiliHttpTransport(
            factory,
            TimeProvider.System,
            delayAsync ?? ((_, _) => Task.CompletedTask));
    }

    private sealed class TrackingMemoryStream(byte[] buffer) : MemoryStream(buffer)
    {
        public bool IsDisposed { get; private set; }

        public int DisposeCalls { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            DisposeCalls++;
            base.Dispose(disposing);
        }

    }
}
