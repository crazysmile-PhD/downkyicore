using System.Collections.Immutable;

namespace DownKyi.Domain.Downloads;

public sealed record DownloadOutput
{
    public DownloadOutput(
        string basePath,
        string? fileSizeText,
        IEnumerable<KeyValuePair<string, string>>? publishedArtifacts = null,
        string? stagingToken = null,
        DownloadPublishingArtifact? publishingArtifact = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        BasePath = basePath;
        FileSizeText = fileSizeText;
        StagingToken = stagingToken ?? Guid.NewGuid().ToString("N");
        if (!Guid.TryParseExact(StagingToken, "N", out _))
        {
            throw new ArgumentException("Staging token must be a GUID.", nameof(stagingToken));
        }
        PublishedArtifacts = publishedArtifacts?.ToImmutableDictionary(
            static entry => entry.Key,
            static entry => entry.Value,
            StringComparer.Ordinal) ?? ImmutableDictionary<string, string>.Empty;
        PublishingArtifact = publishingArtifact;
    }

    public string BasePath { get; }

    public string? FileSizeText { get; }

    public string StagingToken { get; }

    public ImmutableDictionary<string, string> PublishedArtifacts { get; }

    public DownloadPublishingArtifact? PublishingArtifact { get; }
}

public sealed record DownloadPublishingArtifact
{
    public DownloadPublishingArtifact(string key, string fileName, long length, string sha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        if (fileName is "." or ".." || Path.GetFileName(fileName) != fileName)
        {
            throw new ArgumentException("Publishing file name must be a single file name.", nameof(fileName));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (sha256.Length != 64 || sha256.Any(static digit => !Uri.IsHexDigit(digit)))
        {
            throw new ArgumentException("Publishing digest must be SHA-256 hex.", nameof(sha256));
        }

        Key = key;
        FileName = fileName;
        Length = length;
        Sha256 = sha256.ToUpperInvariant();
    }

    public string Key { get; }

    public string FileName { get; }

    public long Length { get; }

    public string Sha256 { get; }
}

public sealed record DownloadCompletion(
    long FinishedTimestamp,
    string FinishedTimeText,
    string? MaximumSpeedText);

public sealed record DownloadFailure(
    string Code,
    string Message,
    bool IsTransient);
