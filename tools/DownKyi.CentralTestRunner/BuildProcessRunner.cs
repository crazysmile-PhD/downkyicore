using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace DownKyi.CentralTestRunner;

internal static class BuildProcessRunner
{
    internal static Task<int> BuildProjectAsync(
        string projectPath, string configuration, bool noRestore,
        CancellationToken cancellationToken)
    {
        var directory = OperatingSystem.IsWindows()
            ? Path.GetDirectoryName(Path.GetFullPath(projectPath))
            : null;
        return RunAsync(CreateBuildStartInfo(projectPath, configuration, noRestore),
            cancellationToken, cleanupResourceDirectory: directory);
    }

    internal static ProcessStartInfo CreateBuildStartInfo(
        string projectPath, string configuration, bool noRestore)
    {
        var startInfo = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(configuration);
        startInfo.ArgumentList.Add("--disable-build-servers");
        startInfo.ArgumentList.Add("-nodeReuse:false");
        startInfo.ArgumentList.Add("-p:UseSharedCompilation=false");
        if (noRestore)
        {
            startInfo.ArgumentList.Add("--no-restore");
        }

        return startInfo;
    }

    [SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task",
        Justification = "Async disposal retains the typed owner in this console runner.")]
    internal static async Task<int> RunAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken,
        TimeSpan? cleanupTimeout = null,
        string? cleanupResourceDirectory = null)
    {
        var window = cleanupTimeout ?? TimeSpan.FromSeconds(5);
        await using var owner = await ProcessLifecycleOwner.StartAsync(
            startInfo, TimeSpan.FromSeconds(5), redirectOutput: false,
            cleanupWindow: window).ConfigureAwait(false);
        var outcome = await owner.CompleteAsync(
            Timeout.InfiniteTimeSpan, cancellationToken, cleanupResourceDirectory)
            .ConfigureAwait(false);
        if (!outcome.CleanupSucceeded)
        {
            throw outcome.PrimaryFailure ?? new InvalidOperationException(
                "The owned build process did not complete cleanup.");
        }

        return outcome.Trigger switch
        {
            ProcessLifecycleTrigger.RootExited => outcome.RootExitCode,
            ProcessLifecycleTrigger.Cancelled => throw new OperationCanceledException(cancellationToken),
            ProcessLifecycleTrigger.Faulted => throw outcome.PrimaryFailure ??
                new InvalidOperationException("The owned build process failed."),
            _ => throw new InvalidOperationException("The owned build process returned an unexpected outcome.")
        };
    }
}
