using DownKyi.Application.Settings;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;

namespace DownKyi.Application.Downloads;

public interface IDownloadHistoryStore
{
    Task<OperationResult> AddHistoryAsync(
        DownloadHistoryRecord history,
        CancellationToken cancellationToken);

    Task<DownloadHistoryPage> GetHistoryPageAsync(
        DownloadHistoryCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken,
        DownloadFinishedSort sort = DownloadFinishedSort.DownloadDesc);

    async Task<IReadOnlyList<DownloadHistoryRecord>> GetHistoryByCidAsync(
        long cid,
        CancellationToken cancellationToken)
    {
        var records = new List<DownloadHistoryRecord>();
        DownloadHistoryCursor? cursor = null;
        do
        {
            var page = await GetHistoryPageAsync(cursor, 500, cancellationToken)
                .ConfigureAwait(false);
            records.AddRange(page.Items.Where(record => record.Cid == cid));
            cursor = page.NextCursor;
        }
        while (cursor != null);

        return records;
    }

    async Task<IReadOnlyList<DownloadHistoryRecord>> GetHistoryByCidsAsync(
        IReadOnlyCollection<long> cids,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cids);
        if (cids.Count == 0)
        {
            return [];
        }

        var requestedCids = cids.ToHashSet();
        var records = new List<DownloadHistoryRecord>();
        DownloadHistoryCursor? cursor = null;
        do
        {
            var page = await GetHistoryPageAsync(cursor, 500, cancellationToken)
                .ConfigureAwait(false);
            records.AddRange(page.Items.Where(record => requestedCids.Contains(record.Cid)));
            cursor = page.NextCursor;
        }
        while (cursor != null);

        return records;
    }

    Task<OperationResult> DeleteHistoryAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken);

    Task<OperationResult> ClearHistoryAsync(CancellationToken cancellationToken);
}
