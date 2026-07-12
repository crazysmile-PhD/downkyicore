using DownKyi.Infrastructure.Time;

namespace DownKyi.Infrastructure.Tests;

public sealed class SystemClockTests
{
    [Fact]
    public void UtcNowUsesTheSystemUtcClock()
    {
        var clock = new SystemClock();
        var before = DateTimeOffset.UtcNow;

        var observed = clock.UtcNow;

        var after = DateTimeOffset.UtcNow;
        Assert.InRange(observed, before, after);
    }

    [Fact]
    public async Task DelayHonorsCancellation()
    {
        var clock = new SystemClock();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            clock.DelayAsync(TimeSpan.FromMinutes(1), cancellation.Token));
    }
}
