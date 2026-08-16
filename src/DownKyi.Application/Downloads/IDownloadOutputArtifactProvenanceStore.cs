using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;

namespace DownKyi.Application.Downloads;

/// <summary>
/// Durable storage for final-output publication evidence.
/// </summary>
/// <remarks>
/// Implementations must not infer rows from task paths or transfer-file state.
/// A missing row is intentionally distinct from a failed read.
/// </remarks>
public interface IDownloadOutputArtifactProvenanceStore
{
    Task<OperationResult> RecordPublishedAsync(
        DownloadOutputArtifactProvenance provenance,
        CancellationToken cancellationToken);

    Task<OperationResult<IReadOnlyList<DownloadOutputArtifactProvenance>>> GetPublishedAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken);
}
