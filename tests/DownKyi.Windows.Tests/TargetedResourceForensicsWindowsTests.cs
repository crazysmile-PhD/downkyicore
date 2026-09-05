using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DownKyi.CentralTestRunner;
using DownKyi.TestInfrastructure;
using Microsoft.Win32.SafeHandles;

namespace DownKyi.Windows.Tests;

[SupportedOSPlatform("windows")]
public sealed class TargetedResourceForensicsWindowsTests
{
    private const string EnableEnvironmentVariable = "DOWNKYI_TARGETED_RESOURCE_FORENSICS";

    [Fact]
    public void DeleteAccessProbeReportsAllowedForAnUnownedDirectory()
    {
        var targetDirectory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-delete-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(targetDirectory);
        try
        {
            var result = TargetedResourceForensics.ProbeDeleteAccess(targetDirectory);

            Assert.Equal(DeleteAccessState.Allowed, result.State);
            Assert.Equal(0, result.Win32Error);
        }
        finally
        {
            Directory.Delete(targetDirectory);
        }
    }

    [Fact]
    public async Task DeleteAccessProbeTracksAKnownDirectoryOwnerUntilRelease()
    {
        var targetDirectory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-delete-owner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(targetDirectory);
        Process? owner = null;
        try
        {
            owner = StartDirectoryLockOwner(targetDirectory);
            await WaitForDirectoryLockReadyAsync(owner).ConfigureAwait(true);

            Assert.Equal(
                DeleteAccessState.SharingViolation,
                TargetedResourceForensics.ProbeDeleteAccess(targetDirectory).State);

            owner.Kill(entireProcessTree: true);
            await owner.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            Assert.True(
                SpinWait.SpinUntil(
                    () => TargetedResourceForensics.ProbeDeleteAccess(targetDirectory).State ==
                        DeleteAccessState.Allowed,
                    TimeSpan.FromSeconds(1)),
                "The controlled directory owner exited without releasing DELETE access.");
        }
        finally
        {
            if (owner is { HasExited: false })
            {
                owner.Kill(entireProcessTree: true);
                await owner.WaitForExitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            }

            owner?.Dispose();
            if (Directory.Exists(targetDirectory) &&
                TargetedResourceForensics.ProbeDeleteAccess(targetDirectory).State == DeleteAccessState.Allowed)
            {
                Directory.Delete(targetDirectory);
            }
        }
    }

    [Fact]
    public async Task DirectoryRundownWaitDoesNotCompleteUntilDeleteAccessIsReady()
    {
        var targetDirectory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-delete-rundown-{Guid.NewGuid():N}");
        Directory.CreateDirectory(targetDirectory);
        Process? owner = null;
        try
        {
            owner = StartDirectoryOwner(targetDirectory);
            await WaitForOwnerReadyAsync(owner).ConfigureAwait(true);
            DuplicateDirectoryHandleIntoProcess(targetDirectory, owner);

            var wait = WindowsDirectoryResourceRundown.WaitForDeleteAccessAsync(
                targetDirectory,
                TimeSpan.FromSeconds(5));

            Assert.False(wait.IsCompleted);
            owner.Kill(entireProcessTree: true);
            await owner.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            await wait.ConfigureAwait(true);

            Assert.Equal(
                DeleteAccessState.Allowed,
                TargetedResourceForensics.ProbeDeleteAccess(targetDirectory).State);
        }
        finally
        {
            await StopOwnerAsync(owner).ConfigureAwait(true);
            if (Directory.Exists(targetDirectory))
            {
                await WindowsDirectoryResourceRundown.WaitForDeleteAccessAsync(
                    targetDirectory,
                    TimeSpan.FromSeconds(5)).ConfigureAwait(true);
                Directory.Delete(targetDirectory);
            }
        }
    }

    [Fact]
    public async Task CancellationCleanupReturnsOnlyAfterDirectoryDeleteAccessIsReady()
    {
        var targetDirectory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-cleanup-rundown-{Guid.NewGuid():N}");
        Directory.CreateDirectory(targetDirectory);
        Process? owner = null;
        try
        {
            owner = StartDirectoryOwner(targetDirectory);
            await WaitForOwnerReadyAsync(owner).ConfigureAwait(true);
            DuplicateDirectoryHandleIntoProcess(targetDirectory, owner);
            Assert.Equal(
                DeleteAccessState.SharingViolation,
                TargetedResourceForensics.ProbeDeleteAccess(targetDirectory).State);

            await BuildProcessRunner.CleanupAfterCancellationAsync(
                owner,
                TimeSpan.FromSeconds(5),
                cleanupResourceDirectory: targetDirectory).ConfigureAwait(true);

            Assert.True(owner.HasExited);
            Assert.Equal(
                DeleteAccessState.Allowed,
                TargetedResourceForensics.ProbeDeleteAccess(targetDirectory).State);
            Directory.Delete(targetDirectory);
        }
        finally
        {
            await StopOwnerAsync(owner).ConfigureAwait(true);
            if (Directory.Exists(targetDirectory))
            {
                await WindowsDirectoryResourceRundown.WaitForDeleteAccessAsync(
                    targetDirectory,
                    TimeSpan.FromSeconds(5)).ConfigureAwait(true);
                Directory.Delete(targetDirectory);
            }
        }
    }

    [Fact]
    public async Task DirectoryRundownDeadlineProducesTypedCleanupFailure()
    {
        var targetDirectory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-rundown-timeout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(targetDirectory);
        Process? owner = null;
        try
        {
            owner = StartDirectoryOwner(targetDirectory);
            await WaitForOwnerReadyAsync(owner).ConfigureAwait(true);
            DuplicateDirectoryHandleIntoProcess(targetDirectory, owner);

            var exception = await Assert.ThrowsAsync<DirectoryResourceRundownTimeoutException>(
                () => WindowsDirectoryResourceRundown.WaitForDeleteAccessAsync(
                    targetDirectory,
                    TimeSpan.Zero)).ConfigureAwait(true);

            Assert.Equal(targetDirectory, exception.ResourcePath);
            Assert.Equal(32, exception.Win32Error);
        }
        finally
        {
            await StopOwnerAsync(owner).ConfigureAwait(true);
            if (Directory.Exists(targetDirectory))
            {
                await WindowsDirectoryResourceRundown.WaitForDeleteAccessAsync(
                    targetDirectory,
                    TimeSpan.FromSeconds(5)).ConfigureAwait(true);
                Directory.Delete(targetDirectory);
            }
        }
    }

    [Fact]
    public async Task CancellationCleanupFailsClosedWhenAnotherOwnerBlocksTheDirectory()
    {
        var targetDirectory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-cleanup-rundown-timeout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(targetDirectory);
        Process? root = null;
        Process? blocker = null;
        try
        {
            root = StartDirectoryOwner(Path.GetTempPath());
            blocker = StartDirectoryOwner(targetDirectory);
            await WaitForOwnerReadyAsync(root).ConfigureAwait(true);
            await WaitForOwnerReadyAsync(blocker).ConfigureAwait(true);
            DuplicateDirectoryHandleIntoProcess(targetDirectory, blocker);

            var exception = await Assert.ThrowsAsync<DirectoryResourceRundownTimeoutException>(
                () => BuildProcessRunner.CleanupAfterCancellationAsync(
                    root,
                    TimeSpan.FromMilliseconds(500),
                    cleanupResourceDirectory: targetDirectory)).ConfigureAwait(true);

            Assert.Equal(targetDirectory, exception.ResourcePath);
            Assert.True(root.HasExited);
            Assert.False(blocker.HasExited);
        }
        finally
        {
            await StopOwnerAsync(root).ConfigureAwait(true);
            await StopOwnerAsync(blocker).ConfigureAwait(true);
            if (Directory.Exists(targetDirectory))
            {
                await WindowsDirectoryResourceRundown.WaitForDeleteAccessAsync(
                    targetDirectory,
                    TimeSpan.FromSeconds(5)).ConfigureAwait(true);
                Directory.Delete(targetDirectory);
            }
        }
    }

    [Fact]
    public async Task CleanupPreservesSnapshotFailureAheadOfDirectoryRundownTimeout()
    {
        var targetDirectory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-rundown-preservation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(targetDirectory);
        Process? root = null;
        Process? blocker = null;
        try
        {
            root = StartDirectoryOwner(Path.GetTempPath());
            blocker = StartDirectoryOwner(targetDirectory);
            await WaitForOwnerReadyAsync(root).ConfigureAwait(true);
            await WaitForOwnerReadyAsync(blocker).ConfigureAwait(true);
            DuplicateDirectoryHandleIntoProcess(targetDirectory, blocker);
            var snapshotFailure = new InvalidOperationException("intentional snapshot failure");

            var exception = await Record.ExceptionAsync(
                () => BuildProcessRunner.CleanupAfterCancellationAsync(
                    root,
                    TimeSpan.FromSeconds(1),
                    (_, _) => Task.FromException<FinalProcessSnapshot>(snapshotFailure),
                    targetDirectory)).ConfigureAwait(true);

            var aggregate = Assert.IsType<AggregateException>(exception);
            Assert.Same(snapshotFailure, aggregate.InnerExceptions[0]);
            Assert.IsType<DirectoryResourceRundownTimeoutException>(aggregate.InnerExceptions[1]);
            Assert.True(root.HasExited);
            Assert.False(blocker.HasExited);
        }
        finally
        {
            await StopOwnerAsync(root).ConfigureAwait(true);
            await StopOwnerAsync(blocker).ConfigureAwait(true);
            if (Directory.Exists(targetDirectory))
            {
                await WindowsDirectoryResourceRundown.WaitForDeleteAccessAsync(
                    targetDirectory,
                    TimeSpan.FromSeconds(5)).ConfigureAwait(true);
                Directory.Delete(targetDirectory);
            }
        }
    }

    [Fact]
    public async Task ControlledDirectoryOwnerProducesCorrelatedLifecycleArtifact()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(EnableEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var targetDirectory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-targeted-resource-{Guid.NewGuid():N}");
        Directory.CreateDirectory(targetDirectory);
        Process? owner = null;
        TargetedResourceForensics? forensics = null;
        try
        {
            owner = StartDirectoryLockOwner(targetDirectory);
            await WaitForDirectoryLockReadyAsync(owner).ConfigureAwait(true);
            forensics = TargetedResourceForensics.Start(
                targetDirectory,
                nameof(ControlledDirectoryOwnerProducesCorrelatedLifecycleArtifact),
                Environment.ProcessId);
            forensics.AddKnownProcessId(owner.Id, "root-owner");

            Assert.Equal(
                DeleteAccessState.SharingViolation,
                TargetedResourceForensics.ProbeDeleteAccess(targetDirectory).State);
            forensics.MarkCancellationRequested();
            // Controlled fault: publish cleanup-return while the known owner is
            // deliberately alive, then prove the recorder correlates its rundown.
            Assert.Equal(
                DeleteAccessState.SharingViolation,
                forensics.MarkCleanupReturned().State);

            owner.Kill(entireProcessTree: true);
            await owner.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            await forensics.ObservePostCleanupAsync(TimeSpan.FromMilliseconds(500))
                .ConfigureAwait(true);
            Assert.Equal(
                DeleteAccessState.Allowed,
                TargetedResourceForensics.ProbeDeleteAccess(targetDirectory).State);

            var summary = forensics.StopAndFormat(
                forcePreserve: true,
                rootCauseStatus: "Proven for the controlled fixture: the known root owner held DELETE sharing closed.");
            var artifactPath = forensics.ArtifactPath;
            Assert.False(string.IsNullOrWhiteSpace(artifactPath), summary);
            Assert.True(File.Exists(artifactPath), summary);
            Assert.InRange(new FileInfo(artifactPath).Length, 1, 1_000_000);
            var artifact = await File.ReadAllTextAsync(
                artifactPath,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.Contains("operation=DirectoryDeleteAccess", artifact, StringComparison.Ordinal);
            Assert.Contains("knownProcessIds=", artifact, StringComparison.Ordinal);
            Assert.Contains($"{owner.Id}:root-owner", artifact, StringComparison.Ordinal);
            Assert.Contains(
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                artifact,
                StringComparison.Ordinal);
            Assert.Contains(
                owner.Id.ToString(CultureInfo.InvariantCulture),
                artifact,
                StringComparison.Ordinal);
            Assert.Contains("state=SharingViolation", artifact, StringComparison.Ordinal);
            Assert.Contains("state=Allowed", artifact, StringComparison.Ordinal);
            Assert.Contains("failureOrAnomalyUtc=", artifact, StringComparison.Ordinal);
            Assert.Contains("readyForOperationUtc=", artifact, StringComparison.Ordinal);
            Assert.Contains(">Process</EventName>", artifact, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("ParentId", artifact, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("fixture-hold", artifact, StringComparison.Ordinal);
            Assert.Contains(">Create</Opcode>", artifact, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Cleanup", artifact, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Close", artifact, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("rootCauseStatus=Proven for the controlled fixture", artifact, StringComparison.Ordinal);
            Assert.DoesNotContain(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                artifact,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            forensics?.Dispose();
            if (owner is { HasExited: false })
            {
                owner.Kill(entireProcessTree: true);
                await owner.WaitForExitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            }

            owner?.Dispose();
            if (Directory.Exists(targetDirectory))
            {
                Directory.Delete(targetDirectory, recursive: true);
            }
        }
    }

    private static Process StartDirectoryOwner(string workingDirectory)
    {
        var runtimeConfig = Path.Combine(
            AppContext.BaseDirectory,
            "DownKyi.Windows.Tests.runtimeconfig.json");
        var fixtureAssembly = Path.Combine(
            AppContext.BaseDirectory,
            "DownKyi.CentralTestRunner.dll");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("--runtimeconfig");
        startInfo.ArgumentList.Add(runtimeConfig);
        startInfo.ArgumentList.Add(fixtureAssembly);
        startInfo.ArgumentList.Add("fixture-hold");
        return Process.Start(startInfo) ??
            throw new InvalidOperationException("Unable to start the controlled directory owner.");
    }

    private static Process StartDirectoryLockOwner(string workingDirectory)
    {
        var runtimeConfig = Path.Combine(
            AppContext.BaseDirectory,
            "DownKyi.Windows.Tests.runtimeconfig.json");
        var fixtureAssembly = Path.Combine(
            AppContext.BaseDirectory,
            "DownKyi.CentralTestRunner.dll");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("--runtimeconfig");
        startInfo.ArgumentList.Add(runtimeConfig);
        startInfo.ArgumentList.Add(fixtureAssembly);
        startInfo.ArgumentList.Add("fixture-directory-lock");
        startInfo.ArgumentList.Add(workingDirectory);
        return Process.Start(startInfo) ??
            throw new InvalidOperationException("Unable to start the controlled directory lock owner.");
    }

    private static async Task WaitForOwnerReadyAsync(Process owner)
    {
        var ready = await owner.StandardOutput.ReadLineAsync(
                TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.StartsWith("fixture-ready pid=", ready, StringComparison.Ordinal);
    }

    private static async Task WaitForDirectoryLockReadyAsync(Process owner)
    {
        var ready = await owner.StandardOutput.ReadLineAsync(
                TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        if (ready is null)
        {
            var error = await owner.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            Assert.Fail($"Directory lock owner exited before its barrier: {error}");
        }

        Assert.Equal("fixture-lock-ready", ready);
    }

    private static async Task StopOwnerAsync(Process? owner)
    {
        if (owner is null)
        {
            return;
        }

        if (!owner.HasExited)
        {
            owner.Kill(entireProcessTree: true);
            await owner.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
        }

        owner.Dispose();
    }

    private static void DuplicateDirectoryHandleIntoProcess(
        string directory,
        Process targetProcess)
    {
        using var sourceProcess = Process.GetCurrentProcess();
        using var sourceHandle = NativeMethods.CreateFile(
            directory,
            desiredAccess: 0,
            NativeMethods.ShareRead | NativeMethods.ShareWrite,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            NativeMethods.BackupSemantics,
            IntPtr.Zero);
        if (sourceHandle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        if (!NativeMethods.DuplicateHandle(
                sourceProcess.Handle,
                sourceHandle.DangerousGetHandle(),
                targetProcess.Handle,
                out _,
                desiredAccess: 0,
                inheritHandle: false,
                NativeMethods.DuplicateSameAccess))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    private static class NativeMethods
    {
        internal const uint ShareRead = 0x00000001;
        internal const uint ShareWrite = 0x00000002;
        internal const uint OpenExisting = 3;
        internal const uint BackupSemantics = 0x02000000;
        internal const uint DuplicateSameAccess = 0x00000002;

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(
            "kernel32.dll",
            EntryPoint = "CreateFileW",
            ExactSpelling = true,
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DuplicateHandle(
            IntPtr sourceProcessHandle,
            IntPtr sourceHandle,
            IntPtr targetProcessHandle,
            out IntPtr targetHandle,
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            uint options);
    }
}
