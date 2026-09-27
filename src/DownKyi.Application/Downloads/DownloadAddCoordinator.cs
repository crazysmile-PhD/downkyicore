namespace DownKyi.Application.Downloads;

public static class DownloadAddCoordinator
{
    public static async Task<int?> AddToDownloadIfSelectionAcceptedAsync(
        Func<Task<bool>> ensureAdmissionAsync,
        Func<Task<DownloadAddSelection?>> selectDownloadAsync,
        Func<DownloadAddSelection, Task<int>> addToDownloadAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ensureAdmissionAsync);
        ArgumentNullException.ThrowIfNull(selectDownloadAsync);
        ArgumentNullException.ThrowIfNull(addToDownloadAsync);
        if (!await ensureAdmissionAsync().ConfigureAwait(false))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var selection = await selectDownloadAsync().ConfigureAwait(false);
        if (selection == null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return await addToDownloadAsync(selection).ConfigureAwait(false);
    }
}
