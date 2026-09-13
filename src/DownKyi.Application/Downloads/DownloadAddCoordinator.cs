namespace DownKyi.Application.Downloads;

public static class DownloadAddCoordinator
{
    public static async Task<int?> AddToDownloadIfDirectorySelectedAsync(
        Func<Task<bool>> ensureAdmissionAsync,
        Func<Task<string?>> selectDirectoryAsync,
        Func<string, Task<int>> addToDownloadAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ensureAdmissionAsync);
        ArgumentNullException.ThrowIfNull(selectDirectoryAsync);
        ArgumentNullException.ThrowIfNull(addToDownloadAsync);
        if (!await ensureAdmissionAsync().ConfigureAwait(false))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var directory = await selectDirectoryAsync().ConfigureAwait(false);
        if (directory == null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return await addToDownloadAsync(directory).ConfigureAwait(false);
    }
}
