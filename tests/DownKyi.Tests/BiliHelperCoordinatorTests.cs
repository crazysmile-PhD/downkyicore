using DownKyi.Services.Toolbox;

namespace DownKyi.Tests;

public sealed class BiliHelperCoordinatorTests
{
    private readonly BiliHelperCoordinator _coordinator = new();

    [Theory]
    [InlineData(1)]
    [InlineData(170001)]
    [InlineData(455017605)]
    public void VideoIdentifierConversionRoundTrips(long avid)
    {
        var bvid = _coordinator.ConvertAvidToBvid($"av{avid}");

        Assert.NotNull(bvid);
        Assert.Equal($"av{avid}", _coordinator.ConvertBvidToAvid(bvid));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-video-id")]
    public void InvalidVideoIdentifierReturnsNull(string? input)
    {
        Assert.Null(_coordinator.ConvertAvidToBvid(input));
        Assert.Null(_coordinator.ConvertBvidToAvid(input));
    }

    [Fact]
    public async Task DanmakuLookupPreservesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => _coordinator.FindDanmakuSenderAsync("ffffffff", cancellation.Token));
    }
}
