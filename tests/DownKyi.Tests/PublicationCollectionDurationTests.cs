using System.Collections.Concurrent;
using DownKyi.Core.BiliApi.Users.Models;
using DownKyi.Services.UserSpace;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;

namespace DownKyi.Tests;

public sealed class PublicationCollectionDurationTests
{
    [Theory]
    [InlineData(60, 70, 80, "03:30")]
    [InlineData(3600, 70, 80, "01:02:30")]
    [InlineData(86400, 70, 80, "24:02:30")]
    public async Task CollapsedCollectionUsesAllPagesOnceAndKeepsOrdinaryVideoDuration(
        long first, long second, long third, string expected)
    {
        using var settings = new TestSettingsStore();
        var requests = new ConcurrentBag<string>();
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (request, _) =>
            {
                var uri = new Uri(request.RequestAddress);
                if (uri.AbsolutePath.EndsWith("/arc/search", StringComparison.Ordinal))
                {
                    return Task.FromResult(PublicationBody(7, 0, 7));
                }
                requests.Add(uri.Query);
                return Task.FromResult(uri.Query.Contains("page_num=1", StringComparison.Ordinal)
                    ? SeasonBody(7, 1, 3, new Archive(1, first), new Archive(2, second))
                    : SeasonBody(7, 2, 3, new Archive(3, third)));
            }
        };

        var snapshot = await CreateCoordinator(settings, client).LoadPublicationPageAsync(
            42, 1, 30, 0, null, TestContext.Current.CancellationToken);

        Assert.Equal(3, snapshot.TotalCount);
        Assert.Equal(expected, snapshot.Medias[0].Duration);
        Assert.True(snapshot.Medias[0].IsCollection);
        Assert.Equal(3, snapshot.Medias[0].CollectionVideoCount);
        Assert.Equal("共 3 个视频", snapshot.Medias[0].CollectionCountText);
        Assert.False(snapshot.Medias[1].IsCollection);
        Assert.Null(snapshot.Medias[1].CollectionVideoCount);
        Assert.Equal("01:44", snapshot.Medias[1].Duration);
        Assert.Equal(expected, snapshot.Medias[2].Duration);
        Assert.Equal("BV-fixture-0", snapshot.Medias[0].Bvid);
        Assert.Equal(2, requests.Count);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("repeated")]
    [InlineData("changed-total")]
    [InlineData("wrong-collection")]
    [InlineData("zero-duration")]
    [InlineData("invalid-identity")]
    [InlineData("api-failure")]
    [InlineData("timeout")]
    public async Task IncompleteCollectionNeverDisplaysLatestEpisodeOrPartialSum(string failure)
    {
        using var settings = new TestSettingsStore();
        var requests = 0;
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (request, _) =>
            {
                var uri = new Uri(request.RequestAddress);
                if (uri.AbsolutePath.EndsWith("/arc/search", StringComparison.Ordinal))
                {
                    return Task.FromResult(PublicationBody(7, 0));
                }
                Interlocked.Increment(ref requests);
                if (uri.Query.Contains("page_num=1", StringComparison.Ordinal))
                {
                    return Task.FromResult(SeasonBody(7, 1, 3, new Archive(1, 60), new Archive(2, 70)));
                }
                return failure switch
                {
                    "empty" => Task.FromResult(SeasonBody(7, 2, 3)),
                    "repeated" => Task.FromResult(SeasonBody(7, 2, 3, new Archive(1, 60), new Archive(2, 70))),
                    "changed-total" => Task.FromResult(SeasonBody(7, 2, 4, new Archive(3, 80))),
                    "wrong-collection" => Task.FromResult(SeasonBody(8, 2, 3, new Archive(3, 80))),
                    "zero-duration" => Task.FromResult(SeasonBody(7, 2, 3, new Archive(3, 0))),
                    "invalid-identity" => Task.FromResult(SeasonBody(7, 2, 3, new Archive(0, 80))),
                    "api-failure" => Task.FromResult("""{"code":-504,"message":"fixture timeout"}"""),
                    "timeout" => Task.FromException<string>(new TaskCanceledException("fixture connect timeout")),
                    _ => throw new InvalidOperationException("Unknown fixture failure.")
                };
            }
        };

        var snapshot = await CreateCoordinator(settings, client).LoadPublicationPageAsync(
            42, 1, 30, 0, null, TestContext.Current.CancellationToken);

        Assert.Equal("总时长未知", snapshot.Medias[0].Duration);
        Assert.Null(snapshot.Medias[0].CollectionVideoCount);
        Assert.Equal("01:44", snapshot.Medias[1].Duration);
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task DuplicateArchiveIsNotCountedTwice()
    {
        using var settings = new TestSettingsStore();
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (request, _) => Task.FromResult(
                new Uri(request.RequestAddress).AbsolutePath.EndsWith("/arc/search", StringComparison.Ordinal)
                    ? PublicationBody(7)
                    : SeasonBody(7, 1, 2, new Archive(1, 60), new Archive(1, 60), new Archive(2, 70)))
        };
        var snapshot = await CreateCoordinator(settings, client).LoadPublicationPageAsync(
            42, 1, 30, 0, null, TestContext.Current.CancellationToken);
        Assert.Equal("02:10", Assert.Single(snapshot.Medias).Duration);
        Assert.Equal(2, snapshot.Medias[0].CollectionVideoCount);
    }

    [Fact]
    public async Task OrdinaryPublicationsDoNotRequestCollectionDetails()
    {
        using var settings = new TestSettingsStore();
        var requests = 0;
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (request, _) =>
            {
                Assert.EndsWith("/arc/search", new Uri(request.RequestAddress).AbsolutePath, StringComparison.Ordinal);
                Interlocked.Increment(ref requests);
                return Task.FromResult(PublicationBody(0));
            }
        };
        var snapshot = await CreateCoordinator(settings, client).LoadPublicationPageAsync(
            42, 1, 30, 0, null, TestContext.Current.CancellationToken);
        Assert.Equal("01:44", Assert.Single(snapshot.Medias).Duration);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task DirectVideoThatBelongsToACollectionDoesNotFetchTheWholeCollection()
    {
        using var settings = new TestSettingsStore();
        var requests = 0;
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (request, _) =>
            {
                Assert.EndsWith("/arc/search", new Uri(request.RequestAddress).AbsolutePath, StringComparison.Ordinal);
                Interlocked.Increment(ref requests);
                return Task.FromResult(PublicationBody(7).Replace("\"is_avoided\":1", "\"is_avoided\":0", StringComparison.Ordinal));
            }
        };
        var snapshot = await CreateCoordinator(settings, client).LoadPublicationPageAsync(
            42, 1, 30, 0, null, TestContext.Current.CancellationToken);
        var media = Assert.Single(snapshot.Medias);
        Assert.True(media.IsDirectVideo);
        Assert.False(media.IsCollection);
        Assert.Equal("01:44", media.Duration);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task NavigationCancellationStopsCollectionLookup()
    {
        using var settings = new TestSettingsStore();
        using var cancellation = new CancellationTokenSource();
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = async (request, token) =>
            {
                if (new Uri(request.RequestAddress).AbsolutePath.EndsWith("/arc/search", StringComparison.Ordinal))
                {
                    return PublicationBody(7);
                }
                await cancellation.CancelAsync().ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return SeasonBody(7, 1, 0);
            }
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateCoordinator(settings, client).LoadPublicationPageAsync(42, 1, 30, 0, null, cancellation.Token));
    }

    private static UserSpacePageCoordinator CreateCoordinator(TestSettingsStore settings, TestBilibiliApiClient client) =>
        new(settings.Store, new TestWbiKeyProvider(), new TestNavigationService(),
            NullLogger<UserSpacePageCoordinator>.Instance, client);

    private static string PublicationBody(params long[] seasonIds) => JsonConvert.SerializeObject(new
    {
        code = 0,
        data = new SpacePublication
        {
            Page = new SpacePublicationPage { Count = seasonIds.Length, Pn = 1, Ps = 30 },
            List = new SpacePublicationList
            {
                Vlist = seasonIds.Select((id, index) => new SpacePublicationListVideo
                { IsAvoided = id > 0 ? 1 : 0, Mid = 42, SeasonId = id, Bvid = $"BV-fixture-{index}", Length = "01:44" }).ToArray()
            }
        }
    });

    private static string SeasonBody(long seasonId, int page, int total, params Archive[] archives) =>
        JsonConvert.SerializeObject(new
        {
            code = 0,
            data = new SpaceSeasonsDetail
            {
                Meta = new SpaceSeasonsMeta { SeasonId = seasonId },
                Page = new SpaceSeasonsSeriesPage { PageNum = page, PageSize = 2, Total = total },
                Archives = archives.Select(item => new SpaceSeasonsSeriesArchives
                { Aid = item.Aid, Duration = item.Duration }).ToArray()
            }
        });

    private sealed record Archive(long Aid, long Duration);
}
