namespace DownKyi.Tests;

internal sealed record Aria2TlsStartupStep(
    string Name,
    Func<CancellationToken, Task> AcquireAsync,
    Func<Task>? RollbackAsync = null);

internal static class Aria2TlsRuntimeStartup
{
    public static async Task AcquireWithPartialRollbackAsync(
        string stage,
        Func<CancellationToken, Task> acquireAsync,
        Func<Task> rollbackAsync,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        ArgumentNullException.ThrowIfNull(acquireAsync);
        ArgumentNullException.ThrowIfNull(rollbackAsync);
        var exception = await Record.ExceptionAsync(
            () => acquireAsync(cancellationToken)).ConfigureAwait(false);
        if (exception == null)
        {
            return;
        }

        var failures = new Aria2TlsFailureCollector();
        failures.Capture($"runtime-startup/{stage}", exception);
        await failures.RunAsync(
            $"runtime-startup-rollback/{stage}",
            rollbackAsync).ConfigureAwait(false);
        failures.ThrowIfAny();
    }

    public static async Task RunAsync(
        IReadOnlyList<Aria2TlsStartupStep> steps,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var acquired = new List<Aria2TlsStartupStep>(steps.Count);
        foreach (var step in steps)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(step.Name);
            var exception = await Record.ExceptionAsync(
                () => step.AcquireAsync(cancellationToken)).ConfigureAwait(false);
            if (exception == null)
            {
                if (step.RollbackAsync != null)
                {
                    acquired.Add(step);
                }

                continue;
            }

            var failures = new Aria2TlsFailureCollector();
            failures.Capture($"runtime-startup/{step.Name}", exception);
            for (var index = acquired.Count - 1; index >= 0; index--)
            {
                var acquiredStep = acquired[index];
                await failures.RunAsync(
                    $"runtime-startup-rollback/{acquiredStep.Name}",
                    acquiredStep.RollbackAsync!).ConfigureAwait(false);
            }

            failures.ThrowIfAny();
            throw new InvalidOperationException("Unreachable startup failure path.");
        }
    }
}
