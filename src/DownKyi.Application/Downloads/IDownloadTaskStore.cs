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

    Task<DownloadTask?> FindAsync(DownloadTaskId taskId, CancellationToken cancellationToken);

    Task<IReadOnlyList<DownloadTask>> GetUnfinishedAsync(CancellationToken cancellationToken);

    async Task<IReadOnlyList<DownloadTask>> GetUnfinishedByCidAsync(
        long cid,
        CancellationToken cancellationToken) =>
        (await GetUnfinishedAsync(cancellationToken).ConfigureAwait(false))
        .Where(task => task.Metadata.Media.Cid == cid)
        .ToArray();

    async Task<IReadOnlyList<DownloadTask>> GetUnfinishedByCidsAsync(
        IReadOnlyCollection<long> cids,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cids);
        if (cids.Count == 0)
        {
            return [];
        }

        var requestedCids = cids.ToHashSet();
        return (await GetUnfinishedAsync(cancellationToken).ConfigureAwait(false))
            .Where(task => requestedCids.Contains(task.Metadata.Media.Cid))
            .ToArray();
    }

    Task<bool> IsOutputPathReservedAsync(
        string basePath,
        bool ignoreCase,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> GetActiveOutputReservationKeysAsync(
        bool ignoreCase,
        CancellationToken cancellationToken);

    async Task<bool> IsOutputDirectoryReservedAsync(
        string directory,
        bool ignoreCase,
        CancellationToken cancellationToken) =>
        (await GetActiveOutputReservationKeysAsync(ignoreCase, cancellationToken).ConfigureAwait(false))
        .Any(key => DownloadOutputPathKey.IsSameOrDescendant(key, directory, ignoreCase));

    async Task<bool> HasOutputClaimConflictAsync(
        string basePath,
        long requestedCid,
        DownloadActionClaims requestedClaims,
        bool ignoreCase,
        CancellationToken cancellationToken)
    {
        var key = DownloadOutputPathKey.Create(basePath, ignoreCase);
        var requestedOwnership = new DownloadOutputOwnership(requestedCid, requestedClaims);
        return (await GetUnfinishedAsync(cancellationToken).ConfigureAwait(false)).Any(task =>
            string.Equals(
                DownloadOutputPathKey.Create(task.Output.BasePath, ignoreCase),
                key,
                StringComparison.Ordinal)
            && new DownloadOutputOwnership(
                    task.Metadata.Media.Cid,
                    task.Plan.ActionClaims)
                .ConflictsWith(requestedOwnership));
    }

    Task<OperationResult> DeleteAsync(DownloadTaskId taskId, CancellationToken cancellationToken);

    Task<IReadOnlyList<QuarantinedDownloadRecord>> GetQuarantinedRecordsAsync(
        CancellationToken cancellationToken);
}
