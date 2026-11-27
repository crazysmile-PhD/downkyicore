using DownKyi.Application.Settings;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;

namespace DownKyi.Application.Downloads;

public sealed class DownloadHistoryService : IDownloadHistoryService
{
    private readonly IDownloadCompletionStore _completion;
    private readonly IDownloadHistoryStore _history;

    public DownloadHistoryService(
        IDownloadCompletionStore completion,
        IDownloadHistoryStore history)
    {
        _completion = completion ?? throw new ArgumentNullException(nameof(completion));
        _history = history ?? throw new ArgumentNullException(nameof(history));
    }

    public static DownloadHistoryService CreateForSharedStore<TStore>(TStore store)
        where TStore : IDownloadCompletionStore, IDownloadHistoryStore
    {
        ArgumentNullException.ThrowIfNull(store);
        return new DownloadHistoryService(store, store);
    }

    public Task<OperationResult> AddAsync(
        DownloadHistoryRecord history,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(history);
        return _history.AddHistoryAsync(history, cancellationToken);
    }

    public Task<OperationResult> CompleteAsync(
        DownloadTask completedTask,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(completedTask);
        return _completion.CompleteAsync(
            completedTask,
            DownloadHistoryRecord.FromCompletedTask(completedTask),
            expectedVersion,
            cancellationToken);
    }

    public Task<DownloadHistoryPage> GetPageAsync(
        DownloadHistoryCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken,
        DownloadFinishedSort sort = DownloadFinishedSort.DownloadDesc) =>
        _history.GetHistoryPageAsync(cursor, pageSize, cancellationToken, sort);

    public Task<IReadOnlyList<DownloadHistoryRecord>> GetByCidAsync(
        long cid,
        CancellationToken cancellationToken) =>
        _history.GetHistoryByCidAsync(cid, cancellationToken);

    public Task<IReadOnlyList<DownloadHistoryRecord>> GetByCidsAsync(
        IReadOnlyCollection<long> cids,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cids);
        return _history.GetHistoryByCidsAsync(cids, cancellationToken);
    }

    public Task<OperationResult> DeleteAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        return _history.DeleteHistoryAsync(taskId, cancellationToken);
    }

    public Task<OperationResult> ClearAsync(CancellationToken cancellationToken) =>
        _history.ClearHistoryAsync(cancellationToken);
}
