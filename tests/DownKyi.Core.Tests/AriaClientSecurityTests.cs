using System.Net;
using DownKyi.Core.Aria2cNet.Client;
using DownKyi.Core.Aria2cNet.Client.Entity;
using DownKyi.TestInfrastructure;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DownKyi.Core.Tests;

public sealed class AriaClientSecurityTests
{
    [Theory]
    [InlineData("http://192.0.2.10")]
    [InlineData("http://aria.example")]
    public void RemotePlaintextRpcEndpointsAreRejected(string host)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new AriaClient(host, 6800, "test-token"));

        Assert.Contains("must use HTTPS", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://localhost")]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://[::1]")]
    [InlineData("https://aria.example")]
    public void LoopbackHttpAndRemoteHttpsRpcEndpointsAreAccepted(string host)
    {
        _ = new AriaClient(host, 6800, "test-token");
    }

    [Fact]
    public void RpcEndpointUserInformationIsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new AriaClient("https://user:password@aria.example", 6800, "test-token"));
    }

    [Fact]
    public async Task EmptyRpcSecretUsesTheUnauthenticatedAriaContract()
    {
        string? capturedPayload = null;
        var client = new AriaClient(
            "http://localhost",
            6800,
            string.Empty,
            (_, payload) =>
            {
                capturedPayload = payload;
                return Task.FromResult<string?>(
                    "{\"id\":\"test\",\"jsonrpc\":\"2.0\",\"result\":{\"version\":\"1.37.0\",\"enabledFeatures\":[]}}");
            });

        var response = await client.GetAriaVersionAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal("1.37.0", response.Result?.Version);
        var request = JObject.Parse(Assert.IsType<string>(capturedPayload));
        Assert.Empty(Assert.IsType<JArray>(request["params"]));
    }

    [Fact]
    public void WhitespaceOnlyRpcSecretIsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new AriaClient("http://localhost", 6800, "   "));
    }

    [Fact]
    public async Task CapabilityRequestPropagatesCancellationToTheRpcTransport()
    {
        CancellationToken observedToken = default;
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new AriaClient(
            "https://aria.example",
            6800,
            "test-token",
            async (_, _, cancellationToken) =>
            {
                observedToken = cancellationToken;
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                    .ConfigureAwait(false);
                return null;
            });
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);

        var request = client.GetAriaVersionAsync(cancellation.Token);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(cancellation.Token, observedToken);
    }

    [Fact]
    public async Task RemoteHttpsRpcRejectsAnUntrustedCertificate()
    {
        using var certificate = TestCertificateAuthority.CreateSelfSignedServerCertificate();
        var server = new LoopbackTlsFileServer(
            _ => certificate,
            "{}"u8.ToArray());
        await using var serverLifetime = server.ConfigureAwait(false);
        var client = new AriaClient(
            "https://localhost",
            server.Url.Port,
            "test-token");

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetGlobalOptionAsync())
            .ConfigureAwait(true);
    }

    [Fact]
    public async Task RpcTransportDoesNotFollowRedirects()
    {
        var target = new LoopbackHttpServer(_ =>
            new LoopbackResponse(HttpStatusCode.OK, "{}"));
        await using var targetLifetime = target.ConfigureAwait(true);
        var redirect = new LoopbackHttpServer(_ =>
            new LoopbackResponse(
                HttpStatusCode.Redirect,
                Headers: new Dictionary<string, string>
                {
                    ["Location"] = target.Url.AbsoluteUri
                }));
        await using var redirectLifetime = redirect.ConfigureAwait(true);
        var client = new AriaClient(
            $"http://127.0.0.1",
            redirect.Url.Port,
            "test-token");

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetGlobalOptionAsync())
            .ConfigureAwait(true);

        Assert.Equal(1, redirect.RequestCount);
        Assert.Equal(0, target.RequestCount);
    }

    [Fact]
    public void DownloadProxyUsesHttpsScopeWithoutEmbeddingCredentials()
    {
        var option = new AriaSendOption
        {
            HttpsProxy = "http://127.0.0.1:7890/"
        };

        var json = JsonConvert.SerializeObject(option);

        Assert.Contains("\"https-proxy\":\"http://127.0.0.1:7890/\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("all-proxy", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Proxy-Authorization", json, StringComparison.OrdinalIgnoreCase);
    }
}
