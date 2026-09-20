using System.Collections.Concurrent;

namespace DownKyi.TestInfrastructure;

public sealed record LoopbackServiceFailure(
    string Service,
    string Stage,
    Exception Exception);

public sealed class LoopbackServiceFailureSink
{
    private readonly ConcurrentQueue<LoopbackServiceFailure> _failures = new();

    public IReadOnlyList<LoopbackServiceFailure> Failures => _failures.ToArray();

    internal void Run(string service, string stage, Action action)
    {
        var exception = FailurePreservingTestCleanup.CaptureException(action);
        if (exception != null)
        {
            _failures.Enqueue(new LoopbackServiceFailure(service, stage, exception));
        }
    }

    internal async Task RunAsync(string service, string stage, Func<Task> action)
    {
        var exception = await FailurePreservingTestCleanup
            .CaptureExceptionAsync(action)
            .ConfigureAwait(false);
        if (exception != null)
        {
            _failures.Enqueue(new LoopbackServiceFailure(service, stage, exception));
        }
    }
}
