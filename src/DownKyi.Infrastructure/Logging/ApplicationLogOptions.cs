namespace DownKyi.Infrastructure.Logging;

public sealed record ApplicationLogOptions(string LogDirectory)
{
    public const long DefaultMaxFileBytes = 32L * 1024 * 1024;

    public const long DefaultMaxTotalBytes = 512L * 1024 * 1024;

    // GitHub permits 25 MB attachments. Leave room for ZIP overhead and the manifest.
    public const int DefaultMaxExportBytes = 24_000_000;

    public int QueueCapacity { get; init; } = 2048;

    public int RecentEventCapacity { get; init; } = 300;

    public int MaxExportBytes { get; init; } = DefaultMaxExportBytes;

    public long MaxFileBytes { get; init; } = DefaultMaxFileBytes;

    public long MaxTotalBytes { get; init; } = DefaultMaxTotalBytes;

    public TimeSpan MaxRetainedAge { get; init; } = TimeSpan.FromDays(7);

    public TimeSpan MaintenanceInterval { get; init; } = TimeSpan.FromHours(1);
}
