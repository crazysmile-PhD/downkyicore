using DownKyi.Domain.Downloads;

namespace DownKyi.Application.Downloads;

public interface IDownloadOutputReservationService
{
    Task<bool> IsOutputDirectoryReservedAsync(
        string directory,
        bool ignoreCase,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> GetActiveOutputReservationKeysAsync(
        bool ignoreCase,
        CancellationToken cancellationToken);

    Task<bool> HasOutputClaimConflictAsync(
        string basePath,
        long requestedCid,
        DownloadActionClaims requestedClaims,
        bool ignoreCase,
        CancellationToken cancellationToken);
}
