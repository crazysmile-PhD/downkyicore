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

    Task<bool> IsLegacyUpgradeAdmissionBlockedAsync(CancellationToken cancellationToken) =>
        Task.FromResult(false);

    Task<OperationResult> ConfirmLegacyRemoteTasksStoppedAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Success());

    Task<DownloadHistoryPage> GetHistoryPageAsync(
        DownloadHistoryCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken);

    Task<OperationResult> DeleteAsync(DownloadTaskId taskId, CancellationToken cancellationToken);

    Task<OperationResult> ClearHistoryAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<QuarantinedDownloadRecord>> GetQuarantinedRecordsAsync(
        CancellationToken cancellationToken);
}
