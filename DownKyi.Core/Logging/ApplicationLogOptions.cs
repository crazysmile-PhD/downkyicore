namespace DownKyi.Core.Logging;

public sealed record ApplicationLogOptions(string LogDirectory)
{
    public int QueueCapacity { get; init; } = 2048;

    public int RecentEventCapacity { get; init; } = 300;

    public long MaxFileBytes { get; init; } = 1024 * 1024;

    public int MaxRetainedFiles { get; init; } = 30;

    public TimeSpan MaxRetainedAge { get; init; } = TimeSpan.FromDays(14);
}
