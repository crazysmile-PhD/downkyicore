namespace DownKyi.Application.Time;

public interface IClock
{
    DateTimeOffset UtcNow { get; }

    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}
