using DownKyi.Application.Lifetime;

namespace DownKyi.Application.Tests;

public sealed class ApplicationCancellationTests
{
    [Fact]
    public async Task ShutdownCancelsEveryLinkedOperationScope()
    {
        using var cancellation = new ApplicationCancellation();
        using var first = cancellation.CreateOperationScope(TestContext.Current.CancellationToken);
        using var second = cancellation.CreateOperationScope(TestContext.Current.CancellationToken);

        await cancellation.RequestShutdownAsync();

        Assert.True(cancellation.ShutdownToken.IsCancellationRequested);
        Assert.True(first.IsCancellationRequested);
        Assert.True(second.IsCancellationRequested);
    }

    [Fact]
    public void CallerCancellationOnlyCancelsItsOwnOperationScope()
    {
        using var cancellation = new ApplicationCancellation();
        using var caller = new CancellationTokenSource();
        using var scope = cancellation.CreateOperationScope(caller.Token);

        caller.Cancel();

        Assert.True(scope.IsCancellationRequested);
        Assert.False(cancellation.ShutdownToken.IsCancellationRequested);
    }
}
