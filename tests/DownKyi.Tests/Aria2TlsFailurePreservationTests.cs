using DownKyi.TestInfrastructure;

namespace DownKyi.Tests;

public sealed class Aria2TlsFailurePreservationTests
{
    [Fact]
    public void SingleFailureKeepsOriginalIdentityAndStack()
    {
        var collector = new FailurePreservingTestCollector();
        var failure = new InvalidOperationException("TLS test failure");

        collector.Run("test-execution", () => ThrowFromTestSite(failure));

        var actual = Assert.Throws<InvalidOperationException>(collector.ThrowIfAny);
        Assert.Same(failure, actual);
        Assert.Contains(
            nameof(ThrowFromTestSite),
            actual.StackTrace,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryStageFailureRemainsVisibleAndAllStagesRun()
    {
        var collector = new FailurePreservingTestCollector();
        var executed = new List<string>();
        var failures = new Exception[]
        {
            new InvalidOperationException("TLS test failure"),
            new IOException("local service cleanup failure"),
            new IOException("report failure"),
            new TimeoutException("process shutdown failure"),
            new InvalidOperationException("trusted-root cleanup failure"),
            new UnauthorizedAccessException("temporary-directory cleanup failure")
        };
        string[] stages =
        [
            "test-execution",
            "local-service-cleanup",
            "report",
            "process-shutdown",
            "trusted-root-cleanup",
            "temporary-directory-cleanup"
        ];

        for (var index = 0; index < stages.Length; index++)
        {
            var stage = stages[index];
            var failure = failures[index];
            await collector.RunAsync(
                stage,
                () => RecordAndFailAsync(executed, stage, failure)).ConfigureAwait(true);
        }

        Assert.Equal(stages, executed);
        var aggregate = Assert.Throws<AggregateException>(collector.ThrowIfAny);
        Assert.Equal(stages, collector.Failures.Select(failure => failure.Stage));
        Assert.Equal(failures, aggregate.InnerExceptions);
    }

    [Fact]
    public void NestedCleanupFailuresKeepTheirNamedStages()
    {
        var nestedCollector = new FailurePreservingTestCollector();
        nestedCollector.Run(
            "trusted-root-cleanup",
            () => throw new InvalidOperationException("trusted-root cleanup failure"));
        nestedCollector.Run(
            "temporary-directory-cleanup",
            () => throw new IOException("temporary-directory cleanup failure"));
        var nested = Assert.Throws<AggregateException>(
            nestedCollector.ThrowIfAny);
        var outerCollector = new FailurePreservingTestCollector();

        outerCollector.Run("runtime-disposal", () => throw nested);

        var actual = Assert.Throws<AggregateException>(
            outerCollector.ThrowIfAny);
        Assert.Same(nested, actual);
        Assert.Equal(2, actual.InnerExceptions.Count);
    }

    private static void ThrowFromTestSite(Exception exception)
    {
        throw exception;
    }

    private static async Task RecordAndFailAsync(
        List<string> executed,
        string stage,
        Exception failure)
    {
        executed.Add(stage);
        await Task.Yield();
        throw failure;
    }
}
