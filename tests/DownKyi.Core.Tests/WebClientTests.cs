using System.Net;
using BiliWebClient = DownKyi.Core.BiliApi.WebClient;

namespace DownKyi.Core.Tests;

[Collection(WebClientTestCollection.Name)]
public sealed class WebClientTests : IDisposable
{
    public WebClientTests()
    {
        BiliWebClient.SetBuvidForTests();
    }

    [Fact]
    public void RequestWeb_ThrowsClearHttpRequestException_WhenRetriesAreExhausted()
    {
        var calls = 0;
        BiliWebClient.SendOverrideForTests = (_, _) =>
        {
            calls++;
            throw new HttpRequestException("network unavailable");
        };

        var exception = Assert.Throws<HttpRequestException>(() =>
            BiliWebClient.RequestWeb(
                "https://example.com/getLogin",
                retry: 1,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(1, calls);
        Assert.Contains("Request failed after 1 attempts", exception.Message);
        Assert.Contains("https://example.com/getLogin", exception.Message);
    }

    [Fact]
    public void RequestWeb_DoesNotRetry_WhenCancellationIsAlreadyRequested()
    {
        var calls = 0;
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        BiliWebClient.SendOverrideForTests = (_, _) =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            };
        };

        Assert.Throws<OperationCanceledException>(() =>
            BiliWebClient.RequestWeb(
                "https://example.com/getLogin",
                retry: 3,
                cancellationToken: cancellationTokenSource.Token));

        Assert.Equal(0, calls);
    }

    [Fact]
    public void RequestWeb_DoesNotRetry_WhenSendIsCanceled()
    {
        var calls = 0;
        BiliWebClient.SendOverrideForTests = (_, _) =>
        {
            calls++;
            throw new OperationCanceledException();
        };

        Assert.Throws<OperationCanceledException>(() =>
            BiliWebClient.RequestWeb(
                "https://example.com/getLogin",
                retry: 3,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(1, calls);
    }

    [Fact]
    public void BuildRequestUrl_AppendsEncodedQueryParameters()
    {
        var url = BiliWebClient.BuildRequestUrlForTests(
            "https://example.com/api?existing=true",
            "GET",
            new Dictionary<string, object?>
            {
                ["keyword"] = "a b",
                ["empty"] = null
            });

        Assert.Equal("https://example.com/api?existing=true&keyword=a+b&empty=", url);
    }

    [Fact]
    public void RequestWebRejectsNullUrl()
    {
        Assert.Throws<ArgumentNullException>(() =>
            BiliWebClient.RequestWeb(
                null!,
                cancellationToken: TestContext.Current.CancellationToken));
    }

    public void Dispose()
    {
        BiliWebClient.ClearTestOverrides();
    }
}
