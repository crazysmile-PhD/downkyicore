using System;
using System.Threading;
using System.Threading.Tasks;

namespace DownKyi.Services.Download;

internal static class DownloadAddCoordinator
{
    public static async Task<int?> AddToDownloadIfDirectorySelectedAsync(
        Func<Task<string?>> selectDirectoryAsync,
        Func<string, Task<int>> addToDownloadAsync,
        CancellationToken cancellationToken = default)
    {
        var directory = await selectDirectoryAsync().ConfigureAwait(false);
        if (directory == null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return await addToDownloadAsync(directory).ConfigureAwait(false);
    }
}
