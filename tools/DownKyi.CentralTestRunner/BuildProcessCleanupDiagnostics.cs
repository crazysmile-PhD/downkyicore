using System.Collections;

namespace DownKyi.CentralTestRunner;

internal enum BuildProcessCleanupPhase
{
    Snapshot,
    ScopeTermination,
    OutputDrain,
    DirectoryResourceRundown
}

internal static class BuildProcessCleanupDiagnostics
{
    private const string PhaseKey = "DownKyi.BuildProcessCleanup.Phase";
    private const string RootPidKey = "DownKyi.BuildProcessCleanup.RootPid";
    private const string ElapsedMillisecondsKey = "DownKyi.BuildProcessCleanup.ElapsedMilliseconds";

    internal static void Attach(
        Exception exception,
        BuildProcessCleanupPhase phase,
        int rootPid,
        TimeSpan elapsed)
    {
        exception.Data[PhaseKey] = GetDiagnosticName(phase);
        exception.Data[RootPidKey] = rootPid;
        exception.Data[ElapsedMillisecondsKey] = Math.Max(
            0,
            (long)Math.Ceiling(elapsed.TotalMilliseconds));
    }

    internal static IEnumerable<string> Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var pending = new Stack<Exception>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(exception);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current))
            {
                continue;
            }

            if (TryRead(current.Data, PhaseKey, out string? phase) &&
                TryRead(current.Data, RootPidKey, out int rootPid) &&
                TryRead(current.Data, ElapsedMillisecondsKey, out long elapsedMilliseconds))
            {
                yield return
                    $"cleanup phase={phase} rootPid={rootPid} " +
                    $"elapsedMs={elapsedMilliseconds} exception={current.GetType().FullName}";
            }

            if (current is AggregateException aggregate)
            {
                for (var index = aggregate.InnerExceptions.Count - 1; index >= 0; index--)
                {
                    pending.Push(aggregate.InnerExceptions[index]);
                }
            }
            else if (current.InnerException is not null)
            {
                pending.Push(current.InnerException);
            }
        }
    }

    private static bool TryRead<T>(IDictionary data, string key, out T? value)
    {
        if (data[key] is T typed)
        {
            value = typed;
            return true;
        }

        value = default;
        return false;
    }

    private static string GetDiagnosticName(BuildProcessCleanupPhase phase)
    {
        return phase switch
        {
            BuildProcessCleanupPhase.Snapshot => "snapshot",
            BuildProcessCleanupPhase.ScopeTermination => "scope-termination",
            BuildProcessCleanupPhase.OutputDrain => "output-drain",
            BuildProcessCleanupPhase.DirectoryResourceRundown => "directory-rundown",
            _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null)
        };
    }
}
