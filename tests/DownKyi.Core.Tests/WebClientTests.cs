using System.Net;
using DownKyi.Core.BiliApi;
using BiliWebClient = DownKyi.Core.BiliApi.WebClient;

namespace DownKyi.Core.Tests;

public sealed class WebClientTests : IDisposable
{
    private readonly WebClientTestContext _context = new();

    [Fact]
    public void RequestWebThrowsClearHttpRequestExceptionWhenRetriesAreExhausted()
    {
        var calls = 0;
        BiliWebClient.SendOverrideForTests = (_, _) =>
        {
            calls++;
            throw new HttpRequestException("network unavailable");
        };

        var exception = Assert.Throws<BilibiliHttpRequestException>(() =>
            BiliWebClient.RequestWeb(
                "https://example.com/getLogin",
                retry: 1,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(1, calls);
        Assert.Equal(BilibiliHttpFailureKind.Transport, exception.FailureKind);
    }

    [Fact]
    public void RequestWebDoesNotRetryWhenCancellationIsAlreadyRequested()
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
    public void RequestWebDoesNotRetryWhenSendIsCanceled()
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
    public void BuildRequestUrlAppendsEncodedQueryParameters()
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
        _context.Dispose();
    }
}
