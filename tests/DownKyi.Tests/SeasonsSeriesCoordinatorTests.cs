using DownKyi.Core.BiliApi;
using DownKyi.Services.Media;
using DownKyi.Services.UserSpace;

namespace DownKyi.Tests;

public sealed class SeasonsSeriesCoordinatorTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task PreCanceledPageRequestDoesNotStartUserSpaceApiWork(int kindValue)
    {
        var coordinator = new SeasonsSeriesCoordinator(
            new ThrowingDownloadCoordinator(),
            new TestBilibiliApiClient());
        var kind = (SeasonsSeriesKind)kindValue;
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.LoadPageAsync(42, 24, kind, 1, 30, cancellation.Token));
    }

    [Fact]
    public async Task EmptySeriesArchivesStayEmptyWithoutPublicationFallback()
    {
        var requests = new List<string>();
        var coordinator = CreateSeriesCoordinator(requests, detailResponse: """
            { "code": 0, "data": { "aids": [], "archives": [] } }
            """);

        var page = await coordinator.LoadPageAsync(
            42,
            99,
            SeasonsSeriesKind.Series,
            1,
            30,
            TestContext.Current.CancellationToken);

        Assert.Empty(page.Archives);
        Assert.Equal("fixture series", page.Title);
        Assert.Equal(1, page.TotalCount);
        Assert.Collection(
            requests,
            request => Assert.Contains("/x/series/series?series_id=99", request, StringComparison.Ordinal),
            request => Assert.Contains("/x/series/archives?mid=42&series_id=99", request, StringComparison.Ordinal));
        Assert.DoesNotContain(requests, request => request.Contains("/x/space/wbi/arc/search", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SeriesApiFailurePropagatesWithoutPublicationFallback()
    {
        var requests = new List<string>();
        var coordinator = CreateSeriesCoordinator(requests, detailResponse: """
            { "code": -404, "message": "series not found", "data": null }
            """);

        var exception = await Assert.ThrowsAsync<BilibiliApiResponseException>(() => coordinator.LoadPageAsync(
            42,
            99,
            SeasonsSeriesKind.Series,
            1,
            30,
            TestContext.Current.CancellationToken));

        Assert.Equal("GetSeriesDetailAsync", exception.Operation);
        Assert.Equal(-404, exception.Code);
        Assert.DoesNotContain(requests, request => request.Contains("/x/space/wbi/arc/search", StringComparison.Ordinal));
    }

    private static SeasonsSeriesCoordinator CreateSeriesCoordinator(
        List<string> requests,
        string detailResponse)
    {
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (request, _) =>
            {
                requests.Add(request.RequestAddress);
                if (request.RequestAddress.Contains("/x/series/series?", StringComparison.Ordinal))
                {
                    return Task.FromResult("""
                        {
                          "code": 0,
                          "data": {
                            "meta": {
                              "mid": 42,
                              "series_id": 99,
                              "name": "fixture series",
                              "total": 1
                            }
                          }
                        }
                        """);
                }

                if (request.RequestAddress.Contains("/x/series/archives?", StringComparison.Ordinal))
                {
                    return Task.FromResult(detailResponse);
                }

                return Task.FromException<string>(new InvalidOperationException(
                    $"Unexpected Bilibili request: {request.RequestAddress}"));
            }
        };

        return new SeasonsSeriesCoordinator(new ThrowingDownloadCoordinator(), client);
    }

    private sealed class ThrowingDownloadCoordinator : IContentDownloadCoordinator
    {
        public Task<ContentDownloadBatchResult?> AddAsync(
            IReadOnlyList<ContentDownloadItem> items,
            bool onlySelected,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Page loading must not submit a download batch.");
        }
    }
}
