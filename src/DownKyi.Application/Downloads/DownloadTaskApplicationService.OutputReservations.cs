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
}
