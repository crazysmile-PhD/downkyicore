namespace DownKyi.Core.BiliApi.Sign;

public static class WbiRequestExecutor
{
    public static async Task<T> ExecuteAsync<T>(
        IWbiKeyProvider keyProvider,
        Func<WbiKeys, long, T> request,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keyProvider);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var keys = await keyProvider.GetValidKeysAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await InvokeAsync(
                request,
                keys,
                timeProvider,
                cancellationToken).ConfigureAwait(false);
        }
        catch (BilibiliApiResponseException exception) when (exception.Code == -403)
        {
            keys = await keyProvider.RefreshAsync(cancellationToken).ConfigureAwait(false);
            return await InvokeAsync(
                request,
                keys,
                timeProvider,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static Task<T> InvokeAsync<T>(
        Func<WbiKeys, long, T> request,
        WbiKeys keys,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        return Task.Run(
            () => request(keys, timeProvider.GetUtcNow().ToUnixTimeSeconds()),
            cancellationToken);
    }
}
