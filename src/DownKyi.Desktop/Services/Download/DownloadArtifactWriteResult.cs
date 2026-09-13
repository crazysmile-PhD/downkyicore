using System.Collections.Generic;

namespace DownKyi.Services.Download;

internal enum DownloadArtifactWriteStatus
{
    Created,
    NotAvailable
}

internal sealed record DownloadArtifactWriteResult(
    DownloadArtifactWriteStatus Status,
    IReadOnlyList<string> Files)
{
    public static DownloadArtifactWriteResult Created(string file) =>
        new(DownloadArtifactWriteStatus.Created, [file]);

    public static DownloadArtifactWriteResult Created(IReadOnlyList<string> files) =>
        new(DownloadArtifactWriteStatus.Created, files);

    public static DownloadArtifactWriteResult NotAvailable() =>
        new(DownloadArtifactWriteStatus.NotAvailable, []);
}
