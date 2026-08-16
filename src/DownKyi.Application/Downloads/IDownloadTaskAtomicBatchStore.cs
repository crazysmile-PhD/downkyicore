using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;

namespace DownKyi.Application.Downloads;

/// <summary>Optional store capability for one durable batch transaction.</summary>
public interface IDownloadTaskAtomicBatchStore
{
    Task<OperationResult> AddManyAtomicAsync(
        IReadOnlyList<DownloadTask> tasks,
        CancellationToken cancellationToken);
}
