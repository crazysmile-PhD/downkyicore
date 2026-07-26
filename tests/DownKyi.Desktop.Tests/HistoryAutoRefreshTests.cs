using DownKyi.ViewModels;

namespace DownKyi.Desktop.Tests;

public sealed class HistoryAutoRefreshTests
{
    [Theory]
    [InlineData(-1, 30)]
    [InlineData(0, 30)]
    [InlineData(9.99, 10)]
    [InlineData(10, 10)]
    [InlineData(30.126, 30.13)]
    public void ConfiguredIntervalIsNormalized(decimal value, decimal expected)
    {
        Assert.Equal(expected, ViewMyHistoryViewModel.NormalizeAutoRefreshInterval(value));
    }

    [Theory]
    [InlineData(-100, 9.00)]
    [InlineData(-1, 9.99)]
    [InlineData(0, 10.00)]
    [InlineData(1, 10.01)]
    [InlineData(100, 11.00)]
    public void RefreshDelayUsesInclusiveHundredthSecondJitter(int jitter, decimal expectedSeconds)
    {
        var delay = ViewMyHistoryViewModel.CreateAutoRefreshDelay(10m, jitter);

        Assert.Equal(expectedSeconds, (decimal)delay.TotalSeconds);
        Assert.Equal(0, delay.TotalMilliseconds % 10);
    }

    [Theory]
    [InlineData(-101)]
    [InlineData(101)]
    public void RefreshDelayRejectsJitterOutsideOneSecond(int jitter)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ViewMyHistoryViewModel.CreateAutoRefreshDelay(10m, jitter));
    }
}
