using System.Net;
using DownKyi.Core.Tests.Infrastructure;
using BiliWebClient = DownKyi.Core.BiliApi.WebClient;

namespace DownKyi.Core.Tests;

[Collection(WebClientTestCollection.Name)]
public sealed class WebClientLoopbackTests : IDisposable
{
    public WebClientLoopbackTests()
    {
        BiliWebClient.SetBuvidForTests();
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task RequestWebThrowsForHttpFailures(HttpStatusCode statusCode)
    {
        var server = new LoopbackHttpServer(_ => new LoopbackResponse(statusCode));
        await using var serverLifetime = server.ConfigureAwait(false);

        Assert.Throws<HttpRequestException>(() =>
            BiliWebClient.RequestWeb(
                server.Url.ToString(),
                retry: 1,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task RequestWebRetriesAndRejectsEmptyResponses()
    {
        var server = new LoopbackHttpServer(_ => new LoopbackResponse(HttpStatusCode.OK));
        await using var serverLifetime = server.ConfigureAwait(false);

        var exception = Assert.Throws<HttpRequestException>(() =>
            BiliWebClient.RequestWeb(
                server.Url.ToString(),
                retry: 2,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(2, server.RequestCount);
        Assert.Contains("Request failed after 2 attempts", exception.Message);
        Assert.IsType<HttpRequestException>(exception.InnerException);
    }

    [Fact]
    public async Task RequestWebReturnsValidJsonResponse()
    {
        const string json = "{\"code\":0,\"data\":{}}";
        var server = new LoopbackHttpServer(_ =>
            new LoopbackResponse(HttpStatusCode.OK, json));
        await using var serverLifetime = server.ConfigureAwait(false);

        var response = BiliWebClient.RequestWeb(
            server.Url.ToString(),
            retry: 1,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(json, response);
    }

    [Fact]
    public async Task RequestWebDoesNotRetryCanceledSlowResponse()
    {
        var server = new LoopbackHttpServer(_ =>
            new LoopbackResponse(
                HttpStatusCode.OK,
                "{\"code\":0}",
                DelayBeforeResponse: TimeSpan.FromSeconds(5)));
        await using var serverLifetime = server.ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        Assert.ThrowsAny<OperationCanceledException>(() =>
            BiliWebClient.RequestWeb(
                server.Url.ToString(),
                retry: 3,
                cancellationToken: cancellation.Token));

        Assert.Equal(1, server.RequestCount);
    }

    [Theory]
    [InlineData("<!DOCTYPE html><html><body>upstream error</body></html>")]
    [InlineData("{not-json")]
    public async Task RequestJsonRejectsNonJsonPayloads(string body)
    {
        var server = new LoopbackHttpServer(_ =>
            new LoopbackResponse(HttpStatusCode.OK, body));
        await using var serverLifetime = server.ConfigureAwait(false);

        var result = DownKyi.Core.BiliApi.BiliApiRequest.RequestJson<Dictionary<string, object>>(
            server.Url.ToString(),
            referer: null,
            operationName: "test",
            logTag: nameof(WebClientLoopbackTests),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task RequestStreamThrowsWhenContentLengthDoesNotMatchBody()
    {
        var server = new LoopbackHttpServer(_ =>
            new LoopbackResponse(
                HttpStatusCode.OK,
                "partial",
                ContentLength: 128));
        await using var serverLifetime = server.ConfigureAwait(false);

        var response = BiliWebClient.RequestStream(
            server.Url.ToString(),
            cancellationToken: TestContext.Current.CancellationToken);
        await using var responseLifetime = response.ConfigureAwait(false);

        await Assert.ThrowsAnyAsync<IOException>(async () =>
            await response.CopyToAsync(Stream.Null, TestContext.Current.CancellationToken).ConfigureAwait(true)).ConfigureAwait(true);
    }

    public void Dispose()
    {
        BiliWebClient.ClearTestOverrides();
    }
}
