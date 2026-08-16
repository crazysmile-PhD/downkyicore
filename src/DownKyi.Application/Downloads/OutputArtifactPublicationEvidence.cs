namespace DownKyi.Application.Downloads;

/// <summary>
/// Immutable evidence captured from one opened temporary output file before
/// publication. The filesystem identity is deliberately opaque outside its
/// originating provider.
/// </summary>
public sealed record OutputArtifactPublicationEvidence(
    long ByteLength,
    string Sha256,
    string IdentityProvider,
    string FilesystemIdentity);
