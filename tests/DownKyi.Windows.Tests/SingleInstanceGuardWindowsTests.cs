using DownKyi.Platform;

namespace DownKyi.Windows.Tests;

public sealed class SingleInstanceGuardWindowsTests
{
    [Fact]
    public async Task RejectedSecondInstanceActivatesThePrimaryInstance()
    {
        var owner = $"owner-{Guid.NewGuid():N}";
        Assert.True(SingleInstanceGuard.TryAcquire(owner, "repo", out var first));
        using (first)
        {
            var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            first!.SetActivationHandler(() => activated.TrySetResult());

            Assert.False(SingleInstanceGuard.TryAcquire(owner, "repo", out var second));
            second?.Dispose();

            await activated.Task
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task ActivationIsRetainedUntilThePrimaryHandlerIsAttached()
    {
        var owner = $"owner-{Guid.NewGuid():N}";
        Assert.True(SingleInstanceGuard.TryAcquire(owner, "repo", out var first));
        using (first)
        {
            Assert.False(SingleInstanceGuard.TryAcquire(owner, "repo", out var second));
            second?.Dispose();

            Assert.True(SpinWait.SpinUntil(
                () => first!.HasPendingActivation,
                TimeSpan.FromSeconds(5)));
            var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            first!.SetActivationHandler(() => activated.TrySetResult());

            await activated.Task
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
        }
    }
}
