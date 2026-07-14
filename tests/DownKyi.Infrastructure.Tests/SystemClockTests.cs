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
}
