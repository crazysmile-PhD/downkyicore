using DownKyi.Core.BiliApi.Users.Models;
using DownKyi.Services.UserSpace;
using Prism.Events;

namespace DownKyi.Tests;

public sealed class UserSpacePageCoordinatorTests
{
    [Fact]
    public async Task PreCanceledPublicationLoadDoesNotStartApiWork()
    {
        var coordinator = new UserSpacePageCoordinator();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<TaskCanceledException>(() => coordinator.LoadPublicationPageAsync(
            42,
            1,
            30,
            0,
            new EventAggregator(),
            cancellation.Token));
    }

    [Fact]
    public async Task PreCanceledProfileLoadDoesNotStartApiWork()
    {
        var coordinator = new UserSpacePageCoordinator();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => coordinator.LoadMyProfileAsync(42, cancellation.Token));
    }

    [Fact]
    public async Task PreCanceledStatsLoadDoesNotStartApiWork()
    {
        var coordinator = new UserSpacePageCoordinator();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => coordinator.LoadMyStatsAsync(42, cancellation.Token));
    }

    [Fact]
    public async Task PreCanceledBangumiLoadDoesNotStartApiWork()
    {
        var coordinator = new UserSpacePageCoordinator();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<TaskCanceledException>(() => coordinator.LoadBangumiFollowPageAsync(
            42,
            BangumiType.ANIME,
            1,
            15,
            new EventAggregator(),
            cancellation.Token));
    }
}
