using System.Diagnostics.CodeAnalysis;
using DownKyi.Domain.Downloads;

namespace DownKyi.Application.Downloads;

public interface IDownloadTaskLookup
{
    /// <summary>Reads an admitted or restored task's latest committed state.</summary>
    DownloadTask GetRequiredSnapshot(DownloadTaskId taskId);

    bool TryGetSnapshot(DownloadTaskId taskId, [NotNullWhen(true)] out DownloadTask? task);

    Task<DownloadTask?> FindAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DownloadTask>> GetUnfinishedAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<DownloadTask>> GetUnfinishedByCidAsync(
        long cid,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DownloadTask>> GetUnfinishedByCidsAsync(
        IReadOnlyCollection<long> cids,
        CancellationToken cancellationToken);
}
