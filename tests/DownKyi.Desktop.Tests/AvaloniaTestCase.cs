using Xunit.Sdk;
using Xunit.v3;

namespace DownKyi.Desktop.Tests;

internal sealed class AvaloniaTestCase : XunitTestCase, ISelfExecutingXunitTestCase
{
    [Obsolete("Called by xUnit deserialization.")]
    public AvaloniaTestCase()
    {
    }

    public AvaloniaTestCase(XunitTestCase testCase)
        : base(
            testCase.TestMethod,
            testCase.TestCaseDisplayName,
            testCase.UniqueID,
            testCase.Explicit,
            testCase.TestLabel,
            testCase.DisableParallelization,
            testCase.SkipExceptions,
            testCase.SkipReason,
            testCase.SkipType,
            testCase.SkipUnless,
            testCase.SkipWhen,
            testCase.Traits.ToDictionary(
                pair => pair.Key,
                pair => new HashSet<string>(pair.Value, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase),
            testCase.TestMethodArguments,
            testCase.SourceFilePath,
            testCase.SourceLineNumber,
            testCase.Timeout)
    {
    }

    public async ValueTask<RunSummary> Run(
        ExplicitOption explicitOption,
        IMessageBus messageBus,
        object?[] constructorArguments,
        ExceptionAggregator aggregator,
        CancellationTokenSource cancellationTokenSource,
        ParallelMode parallelMode,
        ExecutionScheduler scheduler,
        FixtureMappingManager methodFixtureMappings)
    {
        var fixture = await methodFixtureMappings.GetFixture(typeof(AvaloniaTestSessionFixture)).ConfigureAwait(false)
            as AvaloniaTestSessionFixture
            ?? throw new InvalidOperationException("The Avalonia headless assembly fixture is missing.");

        return await fixture.Session.Dispatch(async () =>
        {
            var tests = await aggregator.RunAsync(CreateTests, []).ConfigureAwait(true);
            return await XunitTestCaseRunner.Instance.Run(
                this,
                tests,
                messageBus,
                aggregator,
                cancellationTokenSource,
                parallelMode,
                scheduler,
                TestCaseDisplayName,
                SkipReason,
                explicitOption,
                constructorArguments,
                methodFixtureMappings).ConfigureAwait(true);
        }, cancellationTokenSource.Token).ConfigureAwait(false);
    }
}
