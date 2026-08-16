using System.Collections.Generic;
using System.Linq;

namespace DownKyi.Services.Download;

/// <summary>
/// The outcome for one final-output cleanup candidate. A preserved result is
/// intentionally not a cleanup failure: the caller may remove the task after
/// recording that DownKyi did not have deletion authority for the file.
/// </summary>
internal enum DownloadOutputArtifactCleanupStatus
{
    Deleted,
    Missing,
    PreservedUnproven,
    PreservedModified,
    PreservedReplaced,
    PreservedUnsupported,
    Failed
}

internal sealed record DownloadOutputArtifactCleanupEntry(
    string? ArtifactKey,
    string Path,
    DownloadOutputArtifactCleanupStatus Status);

internal sealed record DownloadOutputArtifactCleanupResult(
    IReadOnlyList<DownloadOutputArtifactCleanupEntry> Entries)
{
    public int DeletedCount => Entries.Count(entry =>
        entry.Status == DownloadOutputArtifactCleanupStatus.Deleted);

    public int MissingCount => Entries.Count(entry =>
        entry.Status == DownloadOutputArtifactCleanupStatus.Missing);

    public int PreservedCount => Entries.Count(entry => entry.Status is
        DownloadOutputArtifactCleanupStatus.PreservedUnproven or
        DownloadOutputArtifactCleanupStatus.PreservedModified or
        DownloadOutputArtifactCleanupStatus.PreservedReplaced or
        DownloadOutputArtifactCleanupStatus.PreservedUnsupported);

    public int FailedCount => Entries.Count(entry =>
        entry.Status == DownloadOutputArtifactCleanupStatus.Failed);

    public bool Succeeded => FailedCount == 0;
}
