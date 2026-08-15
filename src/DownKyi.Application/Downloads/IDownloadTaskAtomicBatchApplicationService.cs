using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;

namespace DownKyi.Application.Downloads;

/// <summary>
/// Optional application capability for atomically adding multiple
/// download tasks.
///
/// TaskChanged events are published only after the durable batch commit
/// succeeds.
/// </summary>
public interface IDownloadTaskAtomicBatchApplicationService
{
    Task<OperationResult> AddManyAtomicAsync(
        IReadOnlyList<DownloadTask> tasks,
        CancellationToken cancellationToken);
}