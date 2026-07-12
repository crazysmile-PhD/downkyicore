using System.Collections.Immutable;

namespace DownKyi.Domain.Downloads;

public sealed class DownloadTransferState
{
    public DownloadTransferState(
        string? backendIdentity,
        IEnumerable<string> completedFileKeys,
        string? activeContent = null,
        string? statusText = null,
        long maximumBytesPerSecond = 0)
    {
        ArgumentNullException.ThrowIfNull(completedFileKeys);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytesPerSecond);

        BackendIdentity = backendIdentity;
        CompletedFileKeys = completedFileKeys.ToImmutableArray();
        ActiveContent = activeContent;
        StatusText = statusText;
        MaximumBytesPerSecond = maximumBytesPerSecond;
    }

    public static DownloadTransferState Empty { get; } = new(null, []);

    public string? BackendIdentity { get; }

    public ImmutableArray<string> CompletedFileKeys { get; }

    public string? ActiveContent { get; }

    public string? StatusText { get; }

    public long MaximumBytesPerSecond { get; }
}
