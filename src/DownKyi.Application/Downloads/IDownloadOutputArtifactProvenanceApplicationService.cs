using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;

namespace DownKyi.Application.Downloads;

/// <summary>
/// Application boundary for recording and loading final-output provenance.
/// </summary>
public interface IDownloadOutputArtifactProvenanceApplicationService
{
    Task<OperationResult> RecordPublishedAsync(
        DownloadTaskId taskId,
        string artifactKey,
        string artifactKind,
        string canonicalPath,
        OutputArtifactPublicationEvidence publicationEvidence,
        CancellationToken cancellationToken);

    Task<OperationResult<IReadOnlyList<DownloadOutputArtifactProvenance>>> GetPublishedAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken);
}
