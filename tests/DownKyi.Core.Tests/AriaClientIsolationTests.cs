using DownKyi.Core.Aria2cNet.Client;
using DownKyi.Core.Aria2cNet.Client.Entity;
using Newtonsoft.Json.Linq;

namespace DownKyi.Core.Tests;

public sealed class AriaClientIsolationTests
{
    [Fact]
    public async Task ClientsKeepEndpointAndTokenIsolatedAcrossConcurrentCalls()
    {
        var firstRequests = new List<(Uri Endpoint, string Payload)>();
        var secondRequests = new List<(Uri Endpoint, string Payload)>();
        var first = CreateClient("first.example", 31001, "first-token", firstRequests);
        var second = CreateClient("second.example", 32002, "second-token", secondRequests);

        var results = await Task.WhenAll(
            first.AddUriAsync(["https://media.example/first"], new AriaSendOption()),
            second.AddUriAsync(["https://media.example/second"], new AriaSendOption()));

        Assert.Equal("test-gid", results[0].Result);
        Assert.Equal("test-gid", results[1].Result);
        AssertRequest(firstRequests, "first.example", 31001, "first-token");
        AssertRequest(secondRequests, "second.example", 32002, "second-token");
    }

    private static AriaClient CreateClient(
        string host,
        int port,
        string token,
        List<(Uri Endpoint, string Payload)> requests)
    {
        return new AriaClient(
            $"http://{host}",
            port,
            token,
            (endpoint, payload) =>
            {
                requests.Add((endpoint, payload));
                return Task.FromResult<string?>("{\"jsonrpc\":\"2.0\",\"id\":\"test\",\"result\":\"test-gid\"}");
            });
    }

    private static void AssertRequest(
        List<(Uri Endpoint, string Payload)> requests,
        string expectedHost,
        int expectedPort,
        string expectedToken)
    {
        var request = Assert.Single(requests);
        Assert.Equal(expectedHost, request.Endpoint.Host);
        Assert.Equal(expectedPort, request.Endpoint.Port);
        Assert.Equal("/jsonrpc", request.Endpoint.AbsolutePath);
        var payload = JObject.Parse(request.Payload);
        Assert.Equal($"token:{expectedToken}", payload["params"]?[0]?.Value<string>());
    }
}
