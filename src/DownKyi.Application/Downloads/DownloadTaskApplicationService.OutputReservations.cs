using DownKyi.Domain.Downloads;

namespace DownKyi.Application.Downloads;

public sealed partial class DownloadTaskApplicationService
{
    public Task<bool> IsOutputPathReservedAsync(
        string basePath,
        bool ignoreCase,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _store.IsOutputPathReservedAsync(basePath, ignoreCase, cancellationToken);
    }

    public Task<IReadOnlyList<string>> GetActiveOutputReservationKeysAsync(
        bool ignoreCase,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _store.GetActiveOutputReservationKeysAsync(ignoreCase, cancellationToken);
    }

    public Task<bool> HasOutputClaimConflictAsync(
        string basePath,
        DownloadContentSelection requestedContent,
        bool ignoreCase,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        ArgumentNullException.ThrowIfNull(requestedContent);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _store.HasOutputClaimConflictAsync(
            basePath,
            requestedContent,
            ignoreCase,
            cancellationToken);
    }
}
