using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;

namespace DownKyi.Application.Downloads;

/// <summary>Optional capability for atomically adding download tasks.</summary>
public interface IDownloadTaskAtomicBatchApplicationService
{
    Task<OperationResult> AddManyAtomicAsync(
        IReadOnlyList<DownloadTask> tasks,
        CancellationToken cancellationToken);
}
