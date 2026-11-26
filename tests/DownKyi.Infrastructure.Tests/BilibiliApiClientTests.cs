using System.Net;
using DownKyi.Application.Bilibili;
using DownKyi.Infrastructure.Bilibili;
using DownKyi.TestInfrastructure;

namespace DownKyi.Infrastructure.Tests;

public sealed class BilibiliApiClientTests
{
    [Fact]
    public async Task FingerprintTimeoutIsReportedAndTheNextApiRequestCanRecover()
    {
        var fingerprintCalls = 0;
        var apiCalls = 0;
        using var factory = new TestHttpClientFactory((request, _) =>
        {
            if (request.RequestUri?.AbsolutePath == "/x/frontend/finger/spi")
            {
                fingerprintCalls++;
                return fingerprintCalls <= 2
                    ? Task.FromException<HttpResponseMessage>(new TaskCanceledException("Fixture fingerprint timeout."))
                    : BilibiliTestResponses.CompletedJson(
                        body: """{"code":0,"data":{"b_3":"synthetic-3","b_4":"synthetic-4"}}""");
            }

            apiCalls++;
            return BilibiliTestResponses.CompletedJson();
        });
        var transport = new BilibiliHttpTransport(factory, TimeProvider.System, static (_, _) => Task.CompletedTask);
        var client = CreateClient(factory, new EmptyCookieProvider(), new BilibiliBuvidProvider(transport));
        var request = new BilibiliHttpRequest("https://api.bilibili.com/x/web-interface/nav");

        var failed = client.GetStringAsync(request, TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAsync<BilibiliHttpRequestException>(() => failed);

        Assert.Equal(BilibiliHttpFailureKind.Timeout, exception.FailureKind);
        Assert.True(failed.IsFaulted);
        Assert.Equal(2, fingerprintCalls);
        Assert.Equal(0, apiCalls);

        Assert.Equal("{}", await client.GetStringAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(3, fingerprintCalls);
        Assert.Equal(1, apiCalls);
    }

    [Fact]
    public async Task AnonymousRequestDoesNotReadCredentialsOrBuvid()
    {
        using var factory = new TestHttpClientFactory(
            (_, _) => BilibiliTestResponses.CompletedJson());
        var buvid = new StubBuvidProvider();
        var client = CreateClient(factory, new ThrowingCookieProvider(), buvid);
        var request = new BilibiliHttpRequest(
            "https://example.invalid/api",
            includeCredentials: false);

        var result = await client.GetStringAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal("{}", result);
        Assert.Equal(0, buvid.Calls);
    }

    [Fact]
    public async Task CredentialRequestReplacesExistingBuvidCookies()
    {
        string? cookieHeader = null;
        using var factory = new TestHttpClientFactory(
            (request, _) =>
            {
                cookieHeader = Assert.Single(request.Headers.GetValues("Cookie"));
                return BilibiliTestResponses.CompletedJson();
            });
        var client = CreateClient(
            factory,
            new StaticCookieProvider(
                "SESSDATA=fixture-session; buvid3=stale-3; BUVID4=stale-4; "
                + "buvid3=older-3; bili_jct=fixture-csrf"),
            new StubBuvidProvider());

        await client.GetStringAsync(
            new BilibiliHttpRequest("https://example.invalid/api"),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            "SESSDATA=fixture-session; bili_jct=fixture-csrf; "
            + "buvid3=synthetic-3; buvid4=synthetic-4",
            cookieHeader);
    }

    [Fact]
    public async Task DownloadRemovesPartialTemporaryFileWhenContentIsTruncated()
    {
        var server = new LoopbackHttpServer(_ =>
            new LoopbackResponse(
                HttpStatusCode.OK,
                "partial",
                ContentLength: 128));
        await using var serverLifetime = server.ConfigureAwait(false);
        using var factory = TestHttpClientFactory.CreateSockets(useProxy: false);
        var client = CreateClient(factory, new EmptyCookieProvider(), new StubBuvidProvider());
        var output = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-partial-{Guid.NewGuid():N}.bin");

        try
        {
            await Assert.ThrowsAnyAsync<IOException>(() => client.DownloadFileAsync(
                new BilibiliHttpRequest(
                    server.Url.ToString(),
                    includeCredentials: false,
                    attempts: 1),
                output,
                TestContext.Current.CancellationToken));

            Assert.False(File.Exists(output));
            Assert.False(File.Exists($"{output}.download"));
        }
        finally
        {
            File.Delete(output);
            File.Delete($"{output}.download");
        }
    }

    private static BilibiliApiClient CreateClient(
        IHttpClientFactory factory,
        IBilibiliCookieProvider cookieProvider,
        IBuvidProvider buvidProvider)
    {
        var transport = new BilibiliHttpTransport(
            factory,
            TimeProvider.System,
            static (_, _) => Task.CompletedTask);
        return new BilibiliApiClient(transport, cookieProvider, buvidProvider);
    }

    private sealed class StaticCookieProvider(string cookieHeader) : IBilibiliCookieProvider
    {
        public string GetCookieHeader() => cookieHeader;
    }
}
