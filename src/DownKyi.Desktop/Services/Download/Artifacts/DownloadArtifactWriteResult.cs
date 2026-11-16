using System.Collections.Generic;
using System.Linq;

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

internal sealed record DownloadSubtitleWriteResult
{
    public DownloadSubtitleWriteResult(
        IReadOnlyDictionary<long, string> trackFiles,
        string? defaultFile)
    {
        ArgumentNullException.ThrowIfNull(trackFiles);
        TrackFiles = trackFiles;
        DefaultFile = defaultFile;
        Files = defaultFile == null
            ? trackFiles.Values.ToArray()
            : trackFiles.Values.Append(defaultFile).ToArray();
    }

    public IReadOnlyDictionary<long, string> TrackFiles { get; }

    public string? DefaultFile { get; }

    public IReadOnlyList<string> Files { get; }

    public static DownloadSubtitleWriteResult Created(
        IReadOnlyDictionary<long, string> trackFiles,
        string? defaultFile) =>
        new(trackFiles, defaultFile);

    public static DownloadSubtitleWriteResult NotAvailable() =>
        new(new Dictionary<long, string>(), defaultFile: null);
}
