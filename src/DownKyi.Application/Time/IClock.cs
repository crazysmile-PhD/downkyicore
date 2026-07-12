namespace DownKyi.Application.Time;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
