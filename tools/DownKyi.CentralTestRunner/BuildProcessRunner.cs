using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;

namespace DownKyi.CentralTestRunner;

internal static class BuildProcessRunner
{
    internal static async Task<int> BuildProjectAsync(
        string projectPath,
        string configuration,
        bool noRestore,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateBuildStartInfo(projectPath, configuration, noRestore);
        var cleanupResourceDirectory = OperatingSystem.IsWindows()
            ? Path.GetDirectoryName(Path.GetFullPath(projectPath))
            : null;

        return await RunAsync(
            startInfo,
            cancellationToken,
            cleanupResourceDirectory: cleanupResourceDirectory).ConfigureAwait(false);
    }

    internal static ProcessStartInfo CreateBuildStartInfo(
        string projectPath,
        string configuration,
        bool noRestore)
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

    internal static async Task<int> RunAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken,
        TimeSpan? cleanupTimeout = null,
        string? cleanupResourceDirectory = null,
        Func<int, TimeSpan, Task<FinalProcessSnapshot>>? captureSnapshotAsync = null)
    {
        var cleanupWindow = cleanupTimeout ?? TimeSpan.FromSeconds(5);
        using var scope = await OwnedProcessScope.StartAsync(startInfo, cleanupWindow)
            .ConfigureAwait(false);
        var process = scope.Host;
        var outputTask = ForwardOutputAsync(process.StandardOutput, Console.Out);
        var errorTask = ForwardOutputAsync(process.StandardError, Console.Error);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CleanupAfterCancellationAsync(
                scope,
                cleanupWindow,
                captureSnapshotAsync,
                cleanupResourceDirectory,
                outputTask,
                errorTask).ConfigureAwait(false);
            throw;
        }

        var exitCode = process.ExitCode;
        await ReleaseScopeAsync(
            scope,
            new CleanupDeadline(cleanupWindow),
            cleanupResourceDirectory,
            outputTask,
            errorTask).ConfigureAwait(false);
        return exitCode;
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A diagnostic failure must be retained while mandatory process termination still runs.")]
    internal static async Task CleanupAfterCancellationAsync(
        OwnedProcessScope scope,
        TimeSpan cleanupWindow,
        Func<int, TimeSpan, Task<FinalProcessSnapshot>>? captureSnapshotAsync = null,
        string? cleanupResourceDirectory = null)
    {
        await CleanupAfterCancellationAsync(
            scope,
            cleanupWindow,
            captureSnapshotAsync,
            cleanupResourceDirectory,
            outputTask: null,
            errorTask: null).ConfigureAwait(false);
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A diagnostic failure must be retained while mandatory process termination still runs.")]
    private static async Task CleanupAfterCancellationAsync(
        OwnedProcessScope scope,
        TimeSpan cleanupWindow,
        Func<int, TimeSpan, Task<FinalProcessSnapshot>>? captureSnapshotAsync,
        string? cleanupResourceDirectory,
        Task? outputTask,
        Task? errorTask)
    {
        var deadline = new CleanupDeadline(cleanupWindow);
        var captureSnapshot = captureSnapshotAsync ?? ProcessTreeSnapshot.CaptureAsync;
        ExceptionDispatchInfo? snapshotFailure = null;
        try
        {
            await Task.Run(() => captureSnapshot(scope.RootPid, deadline.SnapshotWindow))
                .WaitAsync(deadline.SnapshotWindow).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            BuildProcessCleanupDiagnostics.Attach(
                exception,
                BuildProcessCleanupPhase.Snapshot,
                scope.RootPid,
                deadline.Elapsed);
            snapshotFailure = ExceptionDispatchInfo.Capture(exception);
        }

        try
        {
            await ReleaseScopeAsync(
                scope,
                deadline,
                cleanupResourceDirectory,
                outputTask,
                errorTask).ConfigureAwait(false);
        }
        catch (Exception cleanupFailure) when (snapshotFailure is not null)
        {
            throw new AggregateException(
                $"The process snapshot failed: {snapshotFailure.SourceException.Message} " +
                $"Mandatory process cleanup also failed: {cleanupFailure.Message}",
                snapshotFailure.SourceException,
                cleanupFailure);
        }

        snapshotFailure?.Throw();
    }

    private static async Task ReleaseScopeAsync(
        OwnedProcessScope scope,
        CleanupDeadline deadline,
        string? cleanupResourceDirectory,
        Task? outputTask,
        Task? errorTask)
    {
        await RunCleanupPhaseAsync(
            () => scope.TerminateAsync(deadline),
            BuildProcessCleanupPhase.ScopeTermination,
            scope,
            deadline).ConfigureAwait(false);
        if (outputTask is not null && errorTask is not null)
        {
            await RunCleanupPhaseAsync(
                () => Task.WhenAll(outputTask, errorTask).WaitAsync(deadline.Remaining),
                BuildProcessCleanupPhase.OutputDrain,
                scope,
                deadline).ConfigureAwait(false);
        }

        if (OperatingSystem.IsWindows() && cleanupResourceDirectory is not null)
        {
            await RunDirectoryRundownPhaseAsync(scope, deadline, cleanupResourceDirectory)
                .ConfigureAwait(false);
        }
    }

    [SupportedOSPlatform("windows")]
    private static Task RunDirectoryRundownPhaseAsync(
        OwnedProcessScope scope,
        CleanupDeadline deadline,
        string resourceDirectory)
    {
        return RunCleanupPhaseAsync(
            () => WindowsDirectoryResourceRundown.WaitForDeleteAccessAsync(
                resourceDirectory,
                deadline.Remaining),
            BuildProcessCleanupPhase.DirectoryResourceRundown,
            scope,
            deadline);
    }

    private static async Task RunCleanupPhaseAsync(
        Func<Task> operation,
        BuildProcessCleanupPhase phase,
        OwnedProcessScope scope,
        CleanupDeadline deadline)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            BuildProcessCleanupDiagnostics.Attach(
                exception,
                phase,
                scope.RootPid,
                deadline.Elapsed);
            throw;
        }
    }

    private static async Task ForwardOutputAsync(StreamReader source, TextWriter destination)
    {
        var buffer = new char[4096];
        while (true)
        {
            var count = await source.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (count == 0)
            {
                return;
            }

            await destination.WriteAsync(buffer.AsMemory(0, count)).ConfigureAwait(false);
        }
    }

}
