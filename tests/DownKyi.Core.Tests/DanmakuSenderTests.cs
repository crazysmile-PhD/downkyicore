using DownKyi.Core.BiliApi.BiliUtils;

namespace DownKyi.Core.Tests;

public sealed class DanmakuSenderTests
{
    [Fact]
    public void LookupRejectsCancellationBeforeCpuSearchStarts()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => DanmakuSender.FindDanmakuSender("ffffffff", cancellation.Token));
    }
}
