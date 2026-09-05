using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DownKyi.Core.Aria2cNet.Server;
using DownKyi.TestInfrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Windows.Tests;

[SupportedOSPlatform("windows")]
public sealed partial class AriaServerWindowsTests
{
    [Fact]
    public async Task LifetimeJobTerminatesTheAssignedProcessWhenReleased()
    {
        var process = StartLongRunningProcess();
        WindowsProcessJob? processJob = null;

        await ExternalProcessTestHarness.RunWithCleanupAsync(
            async () =>
            {
                processJob = WindowsProcessJob.TryCreateAndAssign(
                    process,
                    NullLogger.Instance);

                Assert.NotNull(processJob);
                processJob.Dispose();
                processJob = null;
                await process
                    .WaitForExitAsync(TestContext.Current.CancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)
                    .ConfigureAwait(true);
                Assert.True(process.HasExited);
            },
            () =>
            {
                processJob?.Dispose();
                return Task.CompletedTask;
            },
            () => ExternalProcessTestHarness.StopAsync(process, TimeSpan.FromSeconds(5)),
            () =>
            {
                process.Dispose();
                return Task.CompletedTask;
            });
    }

    [Fact]
    public async Task StartupFailureWaitsForChildExitBeforeDeletingRpcSecret()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-aria-secret-exit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var server = new AriaServer(NullLoggerFactory.Instance);
        Process? process = null;
        AriaRpcSecretFile? secretFile = null;

        await ExternalProcessTestHarness.RunWithCleanupAsync(
            () =>
            {
                secretFile = AriaRpcSecretFile.Create(
                    directory,
                    "fixture-secret",
                    NullLogger.Instance);
                var secretPath = secretFile.Path;
                server.SetStartupSecretForTests(secretFile);
                process = StartLongRunningProcess();
                DuplicateFileHandleIntoProcess(secretPath, process);
                server.SetTrackedServerForTests(process);

                Assert.Throws<IOException>(() => File.Delete(secretPath));

                Assert.True(server.KillTrackedServer("test startup cleanup"));

                Assert.True(process.HasExited);
                Assert.False(File.Exists(secretPath));
                Assert.False(server.HasTrackedServerForTests());
                return Task.CompletedTask;
            },
            () =>
            {
                server.SetStartupSecretForTests(null);
                server.SetTrackedServerForTests(null);
                return Task.CompletedTask;
            },
            () => process is null
                ? Task.CompletedTask
                : ExternalProcessTestHarness.StopAsync(process, TimeSpan.FromSeconds(5)),
            () =>
            {
                secretFile?.Dispose();
                return Task.CompletedTask;
            },
            () =>
            {
                process?.Dispose();
                return Task.CompletedTask;
            },
            () =>
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }

                return Task.CompletedTask;
            });
    }

    private static Process StartLongRunningProcess()
    {
        var startInfo = new ProcessStartInfo(
            "powershell.exe",
            "-NoLogo -NoProfile -NonInteractive -Command Start-Sleep -Seconds 30")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };

        return ExternalProcessTestHarness.Start(startInfo);
    }

    private static void DuplicateFileHandleIntoProcess(
        string path,
        Process targetProcess)
    {
        using var sourceProcess = Process.GetCurrentProcess();
        using var source = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        if (!NativeMethods.DuplicateHandle(
                sourceProcess.Handle,
                source.SafeFileHandle.DangerousGetHandle(),
                targetProcess.Handle,
                out _,
                0,
                inheritHandle: false,
                NativeMethods.DuplicateSameAccess))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    private static partial class NativeMethods
    {
        internal const uint DuplicateSameAccess = 0x00000002;

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool DuplicateHandle(
            IntPtr sourceProcessHandle,
            IntPtr sourceHandle,
            IntPtr targetProcessHandle,
            out IntPtr targetHandle,
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            uint options);
    }
}
