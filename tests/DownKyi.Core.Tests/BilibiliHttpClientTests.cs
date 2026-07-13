using System.Net;
using System.Net.Http.Headers;
using DownKyi.Core.BiliApi;

namespace DownKyi.Core.Tests;

public sealed class BilibiliHttpClientTests
{
    [Fact]
    public void AuthenticationFailureIsNotRetried()
    {
        var calls = 0;
        using var httpClient = new HttpClient();
        var client = new BilibiliHttpClient(httpClient, static (_, _) => { });

        var exception = Assert.Throws<BilibiliHttpRequestException>(() => client.Send(
            CreateRequest,
            attempts: 3,
            (_, _) =>
            {
                calls++;
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            },
            TestContext.Current.CancellationToken));

        Assert.Equal(1, calls);
        Assert.Equal(BilibiliHttpFailureKind.Authentication, exception.FailureKind);
    }

    [Fact]
    public void RateLimitHonorsRetryAfterBeforeRetrying()
    {
        var calls = 0;
        var delays = new List<TimeSpan>();
        using var httpClient = new HttpClient();
        var client = new BilibiliHttpClient(httpClient, (delay, _) => delays.Add(delay));

        var content = client.Send(
            CreateRequest,
            attempts: 2,
            (_, _) =>
            {
                calls++;
                if (calls == 1)
                {
                    var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                    response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
                    return response;
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}")
                };
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("{}", content);
        Assert.Equal(2, calls);
        Assert.Equal(TimeSpan.FromSeconds(7), Assert.Single(delays));
    }

    [Fact]
    public void ServerFailureAndEmptyBodyAreRetried()
    {
        var calls = 0;
        using var httpClient = new HttpClient();
        var client = new BilibiliHttpClient(httpClient, static (_, _) => { });

        var content = client.Send(
            CreateRequest,
            attempts: 3,
            (_, _) => ++calls switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.InternalServerError),
                2 => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(string.Empty) },
                _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"code\":0}") }
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("{\"code\":0}", content);
        Assert.Equal(3, calls);
    }

    [Fact]
    public void CancellationDuringBackoffStopsFurtherAttempts()
    {
        var calls = 0;
        using var cancellation = new CancellationTokenSource();
        using var httpClient = new HttpClient();
        var client = new BilibiliHttpClient(httpClient, (_, _) =>
        {
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
        });

        Assert.Throws<OperationCanceledException>(() => client.Send(
            CreateRequest,
            attempts: 3,
            (_, _) =>
            {
                calls++;
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            },
            cancellation.Token));

        Assert.Equal(1, calls);
    }

    private static HttpRequestMessage CreateRequest()
    {
        return new HttpRequestMessage(HttpMethod.Get, "https://example.com/api");
    }
}
