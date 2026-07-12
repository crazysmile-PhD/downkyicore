namespace DownKyi.Domain.Downloads;

public sealed record DownloadProgress
{
    public DownloadProgress(
        double percentage,
        long? downloadedBytes = null,
        long? totalBytes = null,
        long bytesPerSecond = 0,
        string? downloadedSizeText = null,
        string? speedText = null)
    {
        if (!double.IsFinite(percentage) || percentage is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(percentage));
        }

        if (downloadedBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(downloadedBytes));
        }

        if (totalBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalBytes));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(bytesPerSecond);

        if (downloadedBytes > totalBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(downloadedBytes));
        }

        Percentage = percentage;
        DownloadedBytes = downloadedBytes;
        TotalBytes = totalBytes;
        BytesPerSecond = bytesPerSecond;
        DownloadedSizeText = downloadedSizeText;
        SpeedText = speedText;
    }

    public static DownloadProgress None { get; } = new(0);

    public double Percentage { get; }

    public long? DownloadedBytes { get; }

    public long? TotalBytes { get; }

    public long BytesPerSecond { get; }

    public string? DownloadedSizeText { get; }

    public string? SpeedText { get; }
}
