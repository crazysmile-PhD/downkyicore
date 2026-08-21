using DownKyi.Domain.Downloads;

namespace DownKyi.Application.Downloads;

/// <summary>
/// Durable evidence that one final output artifact was successfully published
/// for a download task.
/// </summary>
/// <remarks>
/// This is deliberately separate from planned or completed transfer files.
/// It is deletion authority only when the filesystem ownership provider can
/// validate every persisted evidence field against the currently opened file.
/// </remarks>
public sealed record DownloadOutputArtifactProvenance
{
    public DownloadOutputArtifactProvenance(
        DownloadTaskId taskId,
        string artifactKey,
        string artifactKind,
        string canonicalPath,
        OutputArtifactPublicationEvidence publicationEvidence,
        DateTimeOffset publishedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPath);
        ArgumentNullException.ThrowIfNull(publicationEvidence);
        ArgumentOutOfRangeException.ThrowIfNegative(publicationEvidence.ByteLength);
        if (!IsCanonicalSha256(publicationEvidence.Sha256))
        {
            throw new ArgumentException(
                "Final-output provenance requires a lowercase hexadecimal SHA-256 digest.",
                nameof(publicationEvidence));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(publicationEvidence.IdentityProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicationEvidence.FilesystemIdentity);
        if (publishedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Final-output provenance timestamps must be UTC.",
                nameof(publishedAtUtc));
        }

        TaskId = taskId;
        ArtifactKey = artifactKey;
        ArtifactKind = artifactKind;
        CanonicalPath = canonicalPath;
        ByteLength = publicationEvidence.ByteLength;
        Sha256 = publicationEvidence.Sha256;
        IdentityProvider = publicationEvidence.IdentityProvider;
        FilesystemIdentity = publicationEvidence.FilesystemIdentity;
        PublishedAtUtc = publishedAtUtc;
    }

    public DownloadTaskId TaskId { get; }

    /// <summary>Stable logical identity, unique within a task.</summary>
    public string ArtifactKey { get; }

    /// <summary>Diagnostic artifact classification; it is not deletion authority.</summary>
    public string ArtifactKind { get; }

    /// <summary>
    /// Normalized absolute final-output path used to open the candidate.
    /// The application service normalizes it before the first durable write;
    /// restored values are preserved verbatim.
    /// </summary>
    public string CanonicalPath { get; }

    public long ByteLength { get; }

    /// <summary>Canonical lowercase hexadecimal SHA-256 digest.</summary>
    public string Sha256 { get; }

    /// <summary>Provider-owned identity format identifier.</summary>
    public string IdentityProvider { get; }

    /// <summary>Provider-owned opaque durable filesystem identity.</summary>
    public string FilesystemIdentity { get; }

    public DateTimeOffset PublishedAtUtc { get; }

    private static bool IsCanonicalSha256(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            if ((character < '0' || character > '9') &&
                (character < 'a' || character > 'f'))
            {
                return false;
            }
        }

        return true;
    }
}
