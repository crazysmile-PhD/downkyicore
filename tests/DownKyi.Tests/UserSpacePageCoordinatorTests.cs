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
            cancellation.Token));
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
