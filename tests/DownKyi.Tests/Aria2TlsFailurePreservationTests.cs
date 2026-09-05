namespace DownKyi.Tests;

public sealed class Aria2TlsFailurePreservationTests
{
    [Fact]
    public void SingleFailureIsRethrownWithItsOriginalIdentityAndStack()
    {
        var collector = new Aria2TlsFailureCollector();
        var primary = new InvalidOperationException("primary TLS failure");

        collector.Run("primary-test", () => ThrowFromPrimarySite(primary));

        var actual = Assert.Throws<InvalidOperationException>(collector.ThrowIfAny);
        Assert.Same(primary, actual);
        Assert.Contains(
            nameof(ThrowFromPrimarySite),
            actual.StackTrace,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task FiveFailuresAllRunAndRemainVisibleInPrimaryFirstOrder()
    {
        var collector = new Aria2TlsFailureCollector();
        var executed = new List<string>();
        var failures = new Exception[]
        {
            new InvalidOperationException("primary TLS failure"),
            new IOException("report failure"),
            new TimeoutException("runtime disposal failure"),
            new InvalidOperationException("trusted-root cleanup failure"),
            new UnauthorizedAccessException("filesystem cleanup failure")
        };
        string[] stages =
        [
            "primary-test",
            "report",
            "runtime-disposal",
            "trusted-root-cleanup",
            "filesystem-cleanup"
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
        var aggregate = Assert.Throws<Aria2TlsMultipleFailuresException>(collector.ThrowIfAny);
        Assert.Equal(stages, aggregate.Failures.Select(failure => failure.Stage));
        Assert.Same(failures[0], aggregate.PrimaryFailure.Exception);
        Assert.Equal(failures, aggregate.InnerExceptions);
        Assert.Equal(
            failures.Select(failure => failure.GetType()),
            aggregate.Failures.Select(failure => failure.Exception.GetType()));
        Assert.All(
            aggregate.Failures,
            failure =>
            {
                Assert.Same(failure.Exception, failure.DispatchInfo.SourceException);
                Assert.Contains(
                    nameof(RecordAndFailAsync),
                    failure.Exception.StackTrace,
                    StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task FailedStageDoesNotPreventLaterSuccessfulStage()
    {
        var collector = new Aria2TlsFailureCollector();
        var laterStageRan = false;

        await collector.RunAsync(
            "report",
            () => Task.FromException(new IOException("report failure"))).ConfigureAwait(true);
        await collector.RunAsync(
            "runtime-disposal",
            () =>
            {
                laterStageRan = true;
                return Task.CompletedTask;
            }).ConfigureAwait(true);

        Assert.True(laterStageRan);
        Assert.Throws<IOException>(collector.ThrowIfAny);
    }

    [Fact]
    public void NestedMultipleFailuresMergeWithoutHidingTheirNamedStages()
    {
        var nestedCollector = new Aria2TlsFailureCollector();
        nestedCollector.Run(
            "runtime-disposal",
            () => throw new TimeoutException("runtime disposal failure"));
        nestedCollector.Run(
            "trusted-root-cleanup",
            () => throw new InvalidOperationException("trusted-root cleanup failure"));
        var nested = Assert.Throws<Aria2TlsMultipleFailuresException>(
            nestedCollector.ThrowIfAny);
        var outerCollector = new Aria2TlsFailureCollector();

        outerCollector.Run("runtime", () => throw nested);

        var actual = Assert.Throws<Aria2TlsMultipleFailuresException>(
            outerCollector.ThrowIfAny);
        Assert.Equal(
            ["runtime-disposal", "trusted-root-cleanup"],
            actual.Failures.Select(failure => failure.Stage));
        Assert.Same(
            nested.PrimaryFailure.DispatchInfo,
            actual.PrimaryFailure.DispatchInfo);
    }

    private static void ThrowFromPrimarySite(Exception exception)
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
