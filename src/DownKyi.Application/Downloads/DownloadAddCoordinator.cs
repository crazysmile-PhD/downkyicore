namespace DownKyi.Application.Downloads;

public static class DownloadAddCoordinator
{
    public static async Task<int?> AddToDownloadIfDirectorySelectedAsync(
        Func<Task<string?>> selectDirectoryAsync,
        Func<string, Task<int>> addToDownloadAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectDirectoryAsync);
        ArgumentNullException.ThrowIfNull(addToDownloadAsync);
        var directory = await selectDirectoryAsync().ConfigureAwait(false);
        if (directory == null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return await addToDownloadAsync(directory).ConfigureAwait(false);
    }
}
