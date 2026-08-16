using System.Threading;
using System.Threading.Tasks;

namespace DownKyi.Application.Downloads;

/// <summary>
/// Captures opaque filesystem evidence for a temporary output and removes a
/// published output only when the same filesystem object still matches its
/// persisted evidence.
/// </summary>
/// <remarks>
/// The <see cref="OutputArtifactPublicationEvidence.FilesystemIdentity"/> value
/// is provider-owned and opaque. Consumers may persist it, but must not
/// interpret, synthesize, or compare it themselves.
/// </remarks>
public interface IOutputArtifactOwnershipProvider
{
    /// <summary>
    /// Captures evidence from the temporary file before it is atomically
    /// published to its final path.
    /// </summary>
    Task<OutputArtifactEvidenceCaptureResult> CapturePublicationEvidenceAsync(
        string temporaryPath,
        CancellationToken cancellationToken);

    /// <summary>
    /// Verifies, without re-hashing, that the object at a final path is still
    /// the captured object after its atomic publication move. False is
    /// fail-closed and must not authorize provenance persistence.
    /// </summary>
    Task<bool> VerifyPublishedObjectIdentityAsync(
        string destinationPath,
        OutputArtifactPublicationEvidence evidence,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(evidence);
        return Task.FromResult(false);
    }

    /// <summary>
    /// Deletes <paramref name="candidatePath"/> only when it remains the
    /// filesystem object described by <paramref name="provenance"/>.
    /// </summary>
    Task<OutputArtifactSafeDeleteResult> DeleteIfOwnedAsync(
        string candidatePath,
        DownloadOutputArtifactProvenance provenance,
        CancellationToken cancellationToken);
}
