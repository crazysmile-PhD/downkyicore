using System.Threading.Tasks;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;

namespace DownKyi.Services.Download;

internal sealed partial class DownloadArtifactWriter
{
    private static OperationResult<DownloadArtifactWriteResult> ArtifactFailure(
        string code,
        string message)
    {
        return OperationResult.Failure<DownloadArtifactWriteResult>(
            OperationError.Unexpected(code, message));
    }

    private Task RecordPublishedArtifactAsync(
        DownloadTaskId taskId,
        string artifactKey,
        string artifactKind,
        string canonicalPath,
        AtomicOutputPublishResult publication)
    {
        return _provenanceRecorder == null
            ? Task.CompletedTask
            : _provenanceRecorder.RecordAfterPublishAsync(
                taskId,
                artifactKey,
                artifactKind,
                canonicalPath,
                publication.PublicationEvidence);
    }

    private static string GetCoverArtifactKind(string transferKey)
    {
        return transferKey == PageCoverTransferKey
            ? PageCoverArtifactKind
            : CoverArtifactKind;
    }
}
