using System.Collections.Immutable;

namespace DownKyi.Domain.Downloads;

public sealed record DownloadOutput
{
    public DownloadOutput(
        string basePath,
        string? fileSizeText,
        IEnumerable<KeyValuePair<string, string>>? publishedArtifacts = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        BasePath = basePath;
        FileSizeText = fileSizeText;
        PublishedArtifacts = publishedArtifacts?.ToImmutableDictionary(
            static entry => entry.Key,
            static entry => entry.Value,
            StringComparer.Ordinal) ?? ImmutableDictionary<string, string>.Empty;
    }

    public string BasePath { get; }

    public string? FileSizeText { get; }

    public ImmutableDictionary<string, string> PublishedArtifacts { get; }
}

public sealed record DownloadCompletion(
    long FinishedTimestamp,
    string FinishedTimeText,
    string? MaximumSpeedText);

public sealed record DownloadFailure(
    string Code,
    string Message,
    bool IsTransient);
