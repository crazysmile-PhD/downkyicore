using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;

namespace DownKyi.Application.Downloads;

public interface IDownloadTaskStore
{
    Task InitializeAsync(CancellationToken cancellationToken);

    Task<OperationResult> AddAsync(DownloadTask task, CancellationToken cancellationToken);

    Task<OperationResult> UpdateAsync(
        DownloadTask task,
        long expectedVersion,
        CancellationToken cancellationToken);

    Task<OperationResult> UpdateProgressAsync(
        DownloadProgressWrite progressWrite,
        CancellationToken cancellationToken);

    Task<DownloadTask?> FindAsync(DownloadTaskId taskId, CancellationToken cancellationToken);

    Task<IReadOnlyList<DownloadTask>> GetUnfinishedAsync(CancellationToken cancellationToken);
    async Task<IReadOnlyList<string>> GetActiveOutputPathsAsync(
        CancellationToken cancellationToken)
    {
        var tasks =
            await GetUnfinishedAsync(cancellationToken)
                .ConfigureAwait(false);

        var paths =
            new string[tasks.Count];

        for (var index = 0; index < tasks.Count; index++)
        {
            paths[index] =
                tasks[index].Output.BasePath;
        }

        return paths;
    }

    Task<bool> IsOutputPathReservedAsync(
        string basePath,
        bool ignoreCase,
        CancellationToken cancellationToken);

    Task<DownloadHistoryPage> GetHistoryPageAsync(
        DownloadHistoryCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken);

    Task<OperationResult> DeleteAsync(DownloadTaskId taskId, CancellationToken cancellationToken);

    Task<OperationResult> ClearHistoryAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<QuarantinedDownloadRecord>> GetQuarantinedRecordsAsync(
        CancellationToken cancellationToken);
}
