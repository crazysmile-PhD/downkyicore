namespace DownKyi.Core.Logging;

public sealed record ApplicationLogOptions(string LogDirectory)
{
    public const long DefaultMaxFileBytes = 32L * 1024 * 1024;

    public const long DefaultMaxTotalBytes = 512L * 1024 * 1024;

    public int QueueCapacity { get; init; } = 2048;

    public int RecentEventCapacity { get; init; } = 300;

    public long MaxFileBytes { get; init; } = DefaultMaxFileBytes;

    public long MaxTotalBytes { get; init; } = DefaultMaxTotalBytes;

    public TimeSpan MaxRetainedAge { get; init; } = TimeSpan.FromDays(7);

    public TimeSpan MaintenanceInterval { get; init; } = TimeSpan.FromHours(1);
}
