using System.Diagnostics;

namespace DownKyi.CentralTestRunner;

internal sealed class CleanupDeadline(TimeSpan budget)
{
    private readonly Stopwatch clock = Stopwatch.StartNew();

    public TimeSpan Remaining => budget > clock.Elapsed ? budget - clock.Elapsed : TimeSpan.Zero;

    public TimeSpan SnapshotWindow => TimeSpan.FromTicks(Math.Min(
        TimeSpan.FromSeconds(1).Ticks,
        Remaining.Ticks / 4));

    public Task WaitAsync(Task task)
    {
        return task.WaitAsync(Remaining);
    }
}
