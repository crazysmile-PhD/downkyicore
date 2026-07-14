using DownKyi.Services.Friends;

namespace DownKyi.Tests;

public sealed class FriendRelationCoordinatorTests
{
    [Fact]
    public async Task PreCanceledRequestsDoNotStartRelationApiWork()
    {
        var coordinator = new FriendRelationCoordinator();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => coordinator.LoadFollowingOverviewAsync(42, true, cancellation.Token));
        await Assert.ThrowsAsync<TaskCanceledException>(
            () => coordinator.LoadFollowingPageAsync(
                42,
                FollowingListKind.All,
                -1,
                1,
                20,
                cancellation.Token));
        await Assert.ThrowsAsync<TaskCanceledException>(
            () => coordinator.LoadFollowerPageAsync(42, 1, 20, cancellation.Token));
    }
}
