using DownKyi.Application.Time;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;

namespace DownKyi.Application.Downloads;

public sealed class DownloadOutputArtifactProvenanceApplicationService
    : IDownloadOutputArtifactProvenanceApplicationService
{
    private readonly IDownloadOutputArtifactProvenanceStore _store;
    private readonly IClock _clock;

    public DownloadOutputArtifactProvenanceApplicationService(
        IDownloadOutputArtifactProvenanceStore store,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        _store = store;
        _clock = clock;
    }

    public Task<OperationResult> RecordPublishedAsync(
        DownloadTaskId taskId,
        string artifactKey,
        string artifactKind,
        string canonicalPath,
        OutputArtifactPublicationEvidence publicationEvidence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPath);
        ArgumentNullException.ThrowIfNull(publicationEvidence);
        var provenance = new DownloadOutputArtifactProvenance(
            taskId,
            artifactKey,
            artifactKind,
            Path.GetFullPath(canonicalPath),
            publicationEvidence,
            _clock.UtcNow);
        return _store.RecordPublishedAsync(provenance, cancellationToken);
    }

    public Task<OperationResult<IReadOnlyList<DownloadOutputArtifactProvenance>>> GetPublishedAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        return _store.GetPublishedAsync(taskId, cancellationToken);
    }
}
