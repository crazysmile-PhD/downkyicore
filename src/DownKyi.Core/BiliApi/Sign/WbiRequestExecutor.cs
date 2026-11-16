namespace DownKyi.Core.BiliApi.Sign;

public static class WbiRequestExecutor
{
    public static async Task<T> ExecuteAsync<T>(
        IWbiKeyProvider keyProvider,
        Func<WbiKeys, long, Task<T>> request,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keyProvider);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var keys = await keyProvider.GetValidKeysAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await request(
                keys,
                timeProvider.GetUtcNow().ToUnixTimeSeconds()).ConfigureAwait(false);
        }
        catch (BilibiliApiResponseException exception) when (exception.Code == -403)
        {
            keys = await keyProvider.RefreshAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return await request(
                keys,
                timeProvider.GetUtcNow().ToUnixTimeSeconds()).ConfigureAwait(false);
        }
    }
}
