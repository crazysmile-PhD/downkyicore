using DownKyi.Core.BiliApi.Sign;

namespace DownKyi.Tests;

internal sealed class TestWbiKeyProvider : IWbiKeyProvider
{
    private static readonly WbiKeys Keys = new(
        "7cd084941338484aae1ad9425b84077c",
        "4932caff0ff746eab6f01bf08b70ac45");

    public Task<WbiKeys> GetValidKeysAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Keys);
    }

    public Task<WbiKeys> RefreshAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Keys);
    }
}
