using DownKyi.Application.Settings;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;

namespace DownKyi.Application.Downloads;

public interface IDownloadHistoryService
{
    Task<OperationResult> AddAsync(
        DownloadHistoryRecord history,
        CancellationToken cancellationToken);

    Task<OperationResult> CompleteAsync(
        DownloadTask completedTask,
        long expectedVersion,
        CancellationToken cancellationToken);

    Task<DownloadHistoryPage> GetPageAsync(
        DownloadHistoryCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken,
        DownloadFinishedSort sort = DownloadFinishedSort.DownloadDesc);

    Task<IReadOnlyList<DownloadHistoryRecord>> GetByCidAsync(
        long cid,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DownloadHistoryRecord>> GetByCidsAsync(
        IReadOnlyCollection<long> cids,
        CancellationToken cancellationToken);

    Task<OperationResult> DeleteAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken);

    Task<OperationResult> ClearAsync(CancellationToken cancellationToken);
}
