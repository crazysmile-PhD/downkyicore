using DownKyi.Core.BiliApi.Users.Models;
using DownKyi.Services.UserSpace;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class UserSpacePageCoordinatorTests
{
    [Fact]
    public async Task PreCanceledPublicationLoadDoesNotStartApiWork()
    {
        using var settings = new TestSettingsStore();
        var coordinator = CreateCoordinator(settings);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.LoadPublicationPageAsync(
            42,
            1,
            30,
            0,
            null,
            cancellation.Token));
    }

    [Fact]
    public void PublicationMappingPreservesServerTotalAndMediaIdentity()
    {
        var publication = new SpacePublication
        {
            Page = new SpacePublicationPage { Count = 35, Pn = 2, Ps = 30 },
            List = new SpacePublicationList
            {
                Vlist =
                [
                    new SpacePublicationListVideo
                    {
                        Aid = 100,
                        Bvid = "BV1fixture01",
                        Title = "fixture",
                        Length = "01:30",
                        Created = 1_700_000_000,
                        Play = 12,
                        Pic = "cover"
                    }
                ]
            }
        };

        var result = UserSpacePageCoordinator.MapPublicationPage(
            publication,
            new TestNavigationService(),
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(35, result.TotalCount);
        Assert.Equal("BV1fixture01", Assert.Single(result.Medias).Bvid);
    }

    [Fact]
    public async Task PreCanceledProfileLoadDoesNotStartApiWork()
    {
        using var settings = new TestSettingsStore();
        var coordinator = CreateCoordinator(settings);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => coordinator.LoadMyProfileAsync(42, cancellation.Token));
    }

    [Fact]
    public async Task PreCanceledStatsLoadDoesNotStartApiWork()
    {
        using var settings = new TestSettingsStore();
        var coordinator = CreateCoordinator(settings);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => coordinator.LoadMyStatsAsync(42, cancellation.Token));
    }

    [Fact]
    public async Task PreCanceledBangumiLoadDoesNotStartApiWork()
    {
        using var settings = new TestSettingsStore();
        var coordinator = CreateCoordinator(settings);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<TaskCanceledException>(() => coordinator.LoadBangumiFollowPageAsync(
            42,
            BangumiType.ANIME,
            1,
            15,
            cancellation.Token));
    }

    private static UserSpacePageCoordinator CreateCoordinator(TestSettingsStore settings)
    {
        return new UserSpacePageCoordinator(
            settings.Store,
            new TestWbiKeyProvider(),
            new TestNavigationService(),
            NullLogger<UserSpacePageCoordinator>.Instance);
    }
}
