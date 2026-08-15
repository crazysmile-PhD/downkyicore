using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;

namespace DownKyi.Application.Downloads;

/// <summary>
/// Optional capability implemented only by stores that can persist the
/// entire batch inside one atomic transaction.
/// </summary>
public interface IDownloadTaskAtomicBatchStore
{
    Task<OperationResult> AddManyAtomicAsync(
        IReadOnlyList<DownloadTask> tasks,
        CancellationToken cancellationToken);
}