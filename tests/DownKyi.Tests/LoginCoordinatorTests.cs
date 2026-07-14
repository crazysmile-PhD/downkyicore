using DownKyi.Services.Account;

namespace DownKyi.Tests;

public sealed class LoginCoordinatorTests
{
    [Fact]
    public async Task RequestLoginUrlPreservesCancellationBeforeNetworkWork()
    {
        var coordinator = new LoginCoordinator();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => coordinator.RequestLoginUrlAsync(cancellation.Token));
    }
}
