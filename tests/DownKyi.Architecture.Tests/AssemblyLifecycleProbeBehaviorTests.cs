using System.Diagnostics;
using System.IO.Pipes;

namespace DownKyi.Architecture.Tests;

public sealed class AssemblyLifecycleProbeBehaviorTests
{
    private const string CapturePipeEnvironmentVariable = "DOWNKYI_FORENSICS_CAPTURE_PIPE";
    private const string MutationEnvironmentVariable = "DOWNKYI_TEST_MUTATE_FORENSICS_LEASE";
    private const byte CaptureCompleted = 0xA5;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan TerminationTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task CaptureOwnerExclusivelyControlsProbeCompletion()
    {
        var lease = new AnonymousPipeServerStream(
            PipeDirection.Out,
            HandleInheritability.Inheritable);
        using var process = StartProbe(lease.GetClientHandleAsString());
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        lease.DisposeLocalCopyOfClientHandle();
        var completed = false;
        try
        {
            if (Environment.GetEnvironmentVariable(MutationEnvironmentVariable) == "1")
            {
                await CompleteAsync(lease).ConfigureAwait(true);
                completed = true;
                await WaitForExitAsync(process).ConfigureAwait(true);
            }
            else
            {
                await Task.Delay(750, TestContext.Current.CancellationToken).ConfigureAwait(true);
            }

            Assert.False(
                process.HasExited,
                "The probe exited before its capture owner completed the evidence transaction.");

            await CompleteAsync(lease).ConfigureAwait(true);
            completed = true;
            await WaitForExitAsync(process).ConfigureAwait(true);
            Assert.Equal(0, process.ExitCode);
            Assert.Contains("\"Success\":true", await output.ConfigureAwait(true), StringComparison.Ordinal);
            Assert.Equal(string.Empty, await error.ConfigureAwait(true));
        }
        finally
        {
            if (!completed)
            {
                await lease.DisposeAsync().ConfigureAwait(true);
            }

            await TerminateIfRunningAsync(process).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task CaptureOwnerDisconnectFailsClosedWithoutLeavingTheProbeAlive()
    {
        var lease = new AnonymousPipeServerStream(
            PipeDirection.Out,
            HandleInheritability.Inheritable);
        using var process = StartProbe(lease.GetClientHandleAsString());
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        lease.DisposeLocalCopyOfClientHandle();
        await lease.DisposeAsync().ConfigureAwait(true);
        try
        {
            await WaitForExitAsync(process).ConfigureAwait(true);

            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains(
                "capture_owner_disconnected",
                await output.ConfigureAwait(true),
                StringComparison.Ordinal);
        }
        finally
        {
            await TerminateIfRunningAsync(process).ConfigureAwait(true);
        }
    }

    private static Process StartProbe(string capturePipeHandle)
    {
        var probePath = GetProbePath();
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = FindRepositoryRoot(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(probePath);
        startInfo.ArgumentList.Add("--assembly");
        startInfo.ArgumentList.Add(probePath);
        startInfo.Environment[CapturePipeEnvironmentVariable] = capturePipeHandle;
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("The lifecycle probe did not start.");
    }

    private static async Task CompleteAsync(AnonymousPipeServerStream lease)
    {
        await lease.WriteAsync(
                new[] { CaptureCompleted },
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await lease.FlushAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        await lease.DisposeAsync().ConfigureAwait(true);
    }

    private static async Task WaitForExitAsync(Process process)
    {
        await process.WaitForExitAsync(TestContext.Current.CancellationToken)
            .WaitAsync(ProbeTimeout, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
    }

    private static async Task TerminateIfRunningAsync(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync(CancellationToken.None)
            .WaitAsync(TerminationTimeout)
            .ConfigureAwait(true);
    }

    private static string GetProbePath()
    {
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var path = Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "DownKyi.AssemblyLifecycleProbe",
            "bin",
            configuration,
            "net10.0",
            "DownKyi.AssemblyLifecycleProbe.dll");
        Assert.True(File.Exists(path), $"The lifecycle probe was not built: {path}");
        return path;
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "DownKyi.sln")))
        {
            current = current.Parent;
        }

        return current?.FullName
            ?? throw new InvalidOperationException("Repository root not found.");
    }
}
