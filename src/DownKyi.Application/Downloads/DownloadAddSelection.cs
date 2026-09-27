using DownKyi.Domain.Downloads;

namespace DownKyi.Application.Downloads;

public sealed record DownloadAddSelection
{
    public DownloadAddSelection(
        string directory,
        DownloadContentSelection requestedContent)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(requestedContent);

        Directory = directory;
        RequestedContent = requestedContent;
    }

    public string Directory { get; }

    public DownloadContentSelection RequestedContent { get; }
}
