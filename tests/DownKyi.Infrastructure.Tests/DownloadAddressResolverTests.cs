using System.Net;
using DownKyi.Infrastructure.Downloads;

namespace DownKyi.Infrastructure.Tests;

public sealed class DownloadAddressResolverTests
{
    private const string UserAgent = "DownKyi-Test/1.0";
    private const string Credentials = "fixture=download-probe";

    [Theory]
    [InlineData("https://bilibili.com/media", true)]
    [InlineData("https://www.bilibili.com/media", true)]
    [InlineData("https://cdn.example/media", false)]
    [InlineData("https://bilibili.com.example/media", false)]
    public async Task ProbeHeadersCarryCredentialsOnlyToBilibili(string address, bool carriesCredentials)
    {
        using var handler = new ProbeHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("bytes=0-0", request.Headers.Range?.ToString());
            Assert.Equal(UserAgent, request.Headers.UserAgent.ToString());
            Assert.Equal("https://www.bilibili.com", Assert.Single(request.Headers.GetValues("Origin")));
            Assert.Equal(new Uri("https://www.bilibili.com"), request.Headers.Referrer);
            Assert.Equal(carriesCredentials, request.Headers.Contains("Cookie"));
            if (carriesCredentials)
            {
                Assert.Equal(Credentials, Assert.Single(request.Headers.GetValues("Cookie")));
            }

            return new HttpResponseMessage(HttpStatusCode.PartialContent);
        });
        using var resolver = new DownloadAddressResolver(handler);

        var result = await resolver.ResolveAsync(address, UserAgent, Credentials, TestContext.Current.CancellationToken);

        var accepted = Assert.IsType<DownloadAddressAccepted>(result);
        Assert.Equal(new Uri(address), accepted.Address);
        Assert.Equal(carriesCredentials, accepted.Headers.CarriesCredentials);
        Assert.Equal(carriesCredentials, accepted.Headers.Headers.Contains($"Cookie: {Credentials}"));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task RelativeSameOriginRedirectRetainsCredentials()
    {
        var calls = 0;
        using var handler = new ProbeHandler((request, _) =>
        {
            Assert.Equal(Credentials, Assert.Single(request.Headers.GetValues("Cookie")));
            return ++calls == 1
                ? Redirect("/resolved")
                : new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var resolver = new DownloadAddressResolver(handler);

        var result = await resolver.ResolveAsync(
            "https://www.bilibili.com/media", UserAgent, Credentials, TestContext.Current.CancellationToken);

        var accepted = Assert.IsType<DownloadAddressAccepted>(result);
        Assert.Equal(new Uri("https://www.bilibili.com/resolved"), accepted.Address);
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData("https://cdn.example/media", "download.transfer.credentialed-redirect")]
    [InlineData("https://api.bilibili.com/media", "download.transfer.credentialed-redirect")]
    [InlineData("https://www.bilibili.com:444/media", "download.transfer.credentialed-redirect")]
    [InlineData("http://www.bilibili.com/media", "download.transfer.insecure-redirect")]
    public async Task UnsafeRedirectIsRejectedBeforeSendingAnotherRequest(string location, string errorCode)
    {
        using var handler = new ProbeHandler((_, _) => Redirect(location));
        using var resolver = new DownloadAddressResolver(handler);

        var result = await resolver.ResolveAsync(
            "https://www.bilibili.com/media", UserAgent, Credentials, TestContext.Current.CancellationToken);

        var rejected = Assert.IsType<DownloadAddressCandidateRejected>(result);
        Assert.Equal(errorCode, rejected.ErrorCode);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task UncredentialedRedirectRebuildsHeadersForTheDestination()
    {
        var calls = 0;
        using var handler = new ProbeHandler((request, _) =>
        {
            Assert.Equal(calls > 0, request.Headers.Contains("Cookie"));
            return ++calls == 1
                ? Redirect("https://www.bilibili.com/resolved")
                : new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var resolver = new DownloadAddressResolver(handler);

        var result = await resolver.ResolveAsync(
            "https://cdn.example/media", UserAgent, Credentials, TestContext.Current.CancellationToken);

        var accepted = Assert.IsType<DownloadAddressAccepted>(result);
        Assert.True(accepted.Headers.CarriesCredentials);
        Assert.Equal(new Uri("https://www.bilibili.com/resolved"), accepted.Address);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task RedirectLoopStopsAfterFiveRedirects()
    {
        using var handler = new ProbeHandler((_, _) => Redirect("/again"));
        using var resolver = new DownloadAddressResolver(handler);

        var result = await resolver.ResolveAsync(
            "https://cdn.example/media", UserAgent, null, TestContext.Current.CancellationToken);

        var rejected = Assert.IsType<DownloadAddressCandidateRejected>(result);
        Assert.Equal("download.transfer.redirect-limit", rejected.ErrorCode);
        Assert.Equal(6, handler.Calls);
    }

    [Theory]
    [InlineData("http://cdn.example/media")]
    [InlineData("/relative")]
    public async Task InsecureAddressIsRejectedWithoutNetworkAccess(string address)
    {
        using var handler = new ProbeHandler((_, _) => throw new InvalidOperationException("Unexpected request."));
        using var resolver = new DownloadAddressResolver(handler);

        var result = await resolver.ResolveAsync(address, UserAgent, null, TestContext.Current.CancellationToken);

        var rejected = Assert.IsType<DownloadAddressCandidateRejected>(result);
        Assert.Equal("download.transfer.insecure-address", rejected.ErrorCode);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("agent\r\nInjected: value", "fixture=value")]
    [InlineData(UserAgent, "fixture=value\r\nInjected: value")]
    public async Task HeaderInjectionIsRejectedBeforeNetworkAccess(string userAgent, string credentials)
    {
        using var handler = new ProbeHandler((_, _) => throw new InvalidOperationException("Unexpected request."));
        using var resolver = new DownloadAddressResolver(handler);

        await Assert.ThrowsAsync<ArgumentException>(() => resolver.ResolveAsync(
            "https://www.bilibili.com/media", userAgent, credentials, TestContext.Current.CancellationToken));

        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task TlsFailureRetainsItsStableCode()
    {
        using var handler = new ProbeHandler((_, _) => throw new HttpRequestException("certificate has expired"));
        using var resolver = new DownloadAddressResolver(handler);

        var result = await resolver.ResolveAsync(
            "https://cdn.example/media", UserAgent, null, TestContext.Current.CancellationToken);

        var rejected = Assert.IsType<DownloadAddressCandidateRejected>(result);
        Assert.Equal("download.transfer.tls.expired", rejected.ErrorCode);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransportCancellationWithoutCallerCancellationIsTypedTimeout(bool wrapped)
    {
        OperationCanceledException transportError = wrapped
            ? new TaskCanceledException("fixture timeout", new TimeoutException("ConnectTimeout"))
            : new OperationCanceledException("fixture transport cancellation");
        using var handler = new ProbeHandler((_, _) => throw transportError);
        using var resolver = new DownloadAddressResolver(handler);

        var result = await resolver.ResolveAsync(
            "https://cdn.example/media", UserAgent, null, TestContext.Current.CancellationToken);

        Assert.IsType<DownloadAddressTimedOut>(result);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ProbeDeadlineBoundsResponseHeaderWait()
    {
        using var handler = new AsyncProbeHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var resolver = new DownloadAddressResolver(
            handler,
            TimeSpan.FromMilliseconds(50));

        var result = await resolver.ResolveAsync(
            "https://cdn.example/media", UserAgent, null, TestContext.Current.CancellationToken);

        Assert.IsType<DownloadAddressTimedOut>(result);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task TransportFailureIsReturnedAsTypedOutcome()
    {
        using var handler = new ProbeHandler((_, _) =>
            throw new HttpRequestException("fixture connection reset"));
        using var resolver = new DownloadAddressResolver(handler);

        var result = await resolver.ResolveAsync(
            "https://cdn.example/media", UserAgent, null, TestContext.Current.CancellationToken);

        Assert.IsType<DownloadAddressTransportFailure>(result);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CancellationRemainsCancellationAndResolverOwnsItsHandler()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new ProbeHandler((_, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using (var resolver = new DownloadAddressResolver(handler))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.ResolveAsync(
                "https://cdn.example/media", UserAgent, null, cancellation.Token));
        }

        Assert.True(handler.IsDisposed);
        Assert.Equal(1, handler.Calls);
    }

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private sealed class ProbeHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> send)
        : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool IsDisposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(send(request, cancellationToken));
        }

        protected override void Dispose(bool disposing)
        {
            IsDisposed |= disposing;
            base.Dispose(disposing);
        }
    }

    private sealed class AsyncProbeHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return send(request, cancellationToken);
        }
    }
}
