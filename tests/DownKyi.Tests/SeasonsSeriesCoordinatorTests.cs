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
        var coordinator = new SeasonsSeriesCoordinator(new ContentDownloadCoordinator());
        var kind = (SeasonsSeriesKind)kindValue;
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => coordinator.LoadPageAsync(42, 24, kind, 1, 30, cancellation.Token));
    }
}
