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

    Task<bool> IsOutputPathReservedAsync(
        string basePath,
        bool ignoreCase,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> GetActiveOutputReservationKeysAsync(
        bool ignoreCase,
        CancellationToken cancellationToken);

    async Task<bool> HasOutputClaimConflictAsync(
        string basePath,
        DownloadContentSelection requestedContent,
        bool ignoreCase,
        CancellationToken cancellationToken)
    {
        var key = DownloadOutputPathKey.Create(basePath, ignoreCase);
        return (await GetUnfinishedAsync(cancellationToken).ConfigureAwait(false)).Any(task =>
            string.Equals(
                DownloadOutputPathKey.Create(task.Output.BasePath, ignoreCase),
                key,
                StringComparison.Ordinal)
            && DownloadOutputClaims.Overlap(task.Plan.RequestedContent, requestedContent));
    }

    Task<bool> IsLegacyUpgradeAdmissionBlockedAsync(CancellationToken cancellationToken) =>
        Task.FromResult(false);

    Task<OperationResult> ConfirmLegacyRemoteTasksStoppedAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Success());

    Task<OperationResult> DeleteAsync(DownloadTaskId taskId, CancellationToken cancellationToken);

    Task<IReadOnlyList<QuarantinedDownloadRecord>> GetQuarantinedRecordsAsync(
        CancellationToken cancellationToken);
}
