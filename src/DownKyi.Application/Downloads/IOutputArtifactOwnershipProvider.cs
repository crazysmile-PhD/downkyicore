using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

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
public abstract record OutputArtifactTemporaryClaim;

public enum OutputArtifactTemporaryClaimStatus
{
    Claimed,
    Unsupported,
    Failed
}

public sealed record OutputArtifactTemporaryClaimResult(
    OutputArtifactTemporaryClaimStatus Status,
    OutputArtifactTemporaryClaim? Claim)
{
    public bool Succeeded =>
        Status == OutputArtifactTemporaryClaimStatus.Claimed && Claim is not null;

    public static OutputArtifactTemporaryClaimResult Claimed(
        OutputArtifactTemporaryClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        return new OutputArtifactTemporaryClaimResult(
            OutputArtifactTemporaryClaimStatus.Claimed,
            claim);
    }

    public static OutputArtifactTemporaryClaimResult Unsupported() =>
        new(OutputArtifactTemporaryClaimStatus.Unsupported, null);

    public static OutputArtifactTemporaryClaimResult Failed() =>
        new(OutputArtifactTemporaryClaimStatus.Failed, null);
}

public interface IOutputArtifactOwnershipProvider
{
    /// <summary>
    /// Claims the exact temporary object while its creation handle is still
    /// open. The returned capability is provider-owned and opaque.
    /// </summary>
    OutputArtifactTemporaryClaimResult ClaimTemporaryObject(
        SafeFileHandle temporaryHandle)
    {
        ArgumentNullException.ThrowIfNull(temporaryHandle);
        return OutputArtifactTemporaryClaimResult.Unsupported();
    }

    /// <summary>
    /// Captures final provenance evidence only when the temporary pathname
    /// still resolves to the previously claimed object.
    /// </summary>
    Task<OutputArtifactEvidenceCaptureResult> CapturePublicationEvidenceAsync(
        string temporaryPath,
        OutputArtifactTemporaryClaim temporaryClaim,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryPath);
        ArgumentNullException.ThrowIfNull(temporaryClaim);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(OutputArtifactEvidenceCaptureResult.Unsupported());
    }

    /// <summary>
    /// Deletes a temporary pathname only when it still resolves to the
    /// previously claimed object. Content changes made by the producer are
    /// deliberately irrelevant to temporary cleanup authority.
    /// </summary>
    Task<OutputArtifactSafeDeleteResult> DeleteTemporaryIfOwnedAsync(
        string temporaryPath,
        OutputArtifactTemporaryClaim temporaryClaim,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryPath);
        ArgumentNullException.ThrowIfNull(temporaryClaim);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(OutputArtifactSafeDeleteResult.Unsupported());
    }

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
