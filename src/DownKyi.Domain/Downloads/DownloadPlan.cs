using System.Collections.Immutable;

namespace DownKyi.Domain.Downloads;

public sealed class DownloadPlan
{
    public DownloadPlan(
        IEnumerable<KeyValuePair<string, bool>> requestedAssets,
        IEnumerable<KeyValuePair<string, string>> transferFiles,
        int streamType)
    {
        ArgumentNullException.ThrowIfNull(requestedAssets);
        ArgumentNullException.ThrowIfNull(transferFiles);

        RequestedAssets = requestedAssets.ToImmutableDictionary(StringComparer.Ordinal);
        TransferFiles = transferFiles.ToImmutableDictionary(StringComparer.Ordinal);
        StreamType = streamType;
    }

    public ImmutableDictionary<string, bool> RequestedAssets { get; }

    public ImmutableDictionary<string, string> TransferFiles { get; }

    public int StreamType { get; }
}
