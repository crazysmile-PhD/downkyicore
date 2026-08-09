using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DownKyi.Core.Aria2cNet.Server;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Core.Tests;

public sealed partial class AriaServerProcessTests
{
    [Fact]
    public void PackagedBinaryIntegrityAcceptsTheManifestDigest()
    {
        using var fixture = AriaBinaryFixture.Create("trusted aria2 binary");

        AriaBinaryIntegrityVerifier.Verify(fixture.ExecutablePath);
    }

    [Fact]
    public void PackagedBinaryIntegrityRejectsAReplacedExecutable()
    {
        using var fixture = AriaBinaryFixture.Create("trusted aria2 binary");
        File.WriteAllText(fixture.ExecutablePath, "replaced aria2 binary");

        var exception = Assert.Throws<InvalidDataException>(
            () => AriaBinaryIntegrityVerifier.Verify(fixture.ExecutablePath));

        Assert.Contains("integrity", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackagedBinaryIntegrityRejectsMissingOrMalformedSidecars()
    {
        using var fixture = AriaBinaryFixture.Create("trusted aria2 binary");
        File.Delete(fixture.ChecksumPath);
        Assert.Throws<FileNotFoundException>(
            () => AriaBinaryIntegrityVerifier.Verify(fixture.ExecutablePath));

        File.WriteAllText(fixture.ChecksumPath, "not-a-sha256");
        Assert.Throws<InvalidDataException>(
            () => AriaBinaryIntegrityVerifier.Verify(fixture.ExecutablePath));
    }

    [Fact]
    public async Task WindowsLifetimeJobTerminatesTheAssignedProcessWhenReleased()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var process = StartLongRunningProcess();
        var processJob = WindowsProcessJob.TryCreateAndAssign(
            process,
            NullLogger.Instance);

        Assert.NotNull(processJob);
        processJob.Dispose();
        await process
            .WaitForExitAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.True(process.HasExited);
    }

    [Fact]
    public void StartArgumentsBindAriaLifetimeToTheParentAndPreserveResumeFiles()
    {
        var config = new AriaConfig
        {
            ListenPort = 35076,
            Token = "test-token",
            LogLevel = AriaConfigLogLevel.WARN,
            MaxConcurrentDownloads = 3,
            MaxConnectionPerServer = 8,
            Split = 5,
            MinSplitSize = 10,
            ContinueDownload = true,
            FileAllocation = AriaConfigFileAllocation.NONE
        };

        var arguments = AriaServer.BuildArguments(
            config,
            "private rpc.conf",
            "aria.session",
            "aria.log",
            saveSessionInterval: 120,
            parentProcessId: 4242);

        Assert.Contains("--conf-path=private rpc.conf", arguments);
        Assert.Contains("--stop-with-process=4242", arguments);
        Assert.Contains("--rpc-listen-all=false", arguments);
        Assert.Contains("--rpc-allow-origin-all=false", arguments);
        Assert.DoesNotContain("--rpc-listen-all=true", arguments);
        Assert.DoesNotContain("--check-certificate=false", arguments);
        Assert.Contains("--input-file=aria.session", arguments);
        Assert.Contains("--save-session=aria.session", arguments);
        Assert.Contains("--continue=true", arguments);
        Assert.DoesNotContain(
            arguments,
            argument => argument.Contains(config.Token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task KillTrackedServerTerminatesAndReleasesTrackedProcess()
    {
        var server = new AriaServer(NullLoggerFactory.Instance);
        using var process = StartLongRunningProcess();
        server.SetTrackedServerForTests(process);

        try
        {
            Assert.True(server.KillTrackedServer("test cleanup"));
            await process
                .WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ConfigureAwait(true);

            Assert.True(process.HasExited);
            Assert.False(server.HasTrackedServerForTests());
        }
        finally
        {
            server.SetTrackedServerForTests(null);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    [Fact]
    public async Task WindowsStartupFailureWaitsForChildExitBeforeDeletingRpcSecret()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-aria-secret-exit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var server = new AriaServer(NullLoggerFactory.Instance);
        Process? process = null;
        try
        {
            using var secretFile = AriaRpcSecretFile.Create(
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
        }
        finally
        {
            server.SetStartupSecretForTests(null);
            server.SetTrackedServerForTests(null);
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(true);
            }

            process?.Dispose();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static Process StartLongRunningProcess()
    {
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo(
                "powershell.exe",
                "-NoLogo -NoProfile -NonInteractive -Command Start-Sleep -Seconds 30")
            : new ProcessStartInfo("/bin/sh", "-c \"exec sleep 30\"");
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;

        return Process.Start(startInfo)
               ?? throw new InvalidOperationException("Could not start the process used by the cleanup test.");
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

    private sealed class AriaBinaryFixture : IDisposable
    {
        private AriaBinaryFixture(string rootPath, string executablePath)
        {
            RootPath = rootPath;
            ExecutablePath = executablePath;
        }

        public string RootPath { get; }

        public string ExecutablePath { get; }

        public string ChecksumPath =>
            ExecutablePath + AriaBinaryIntegrityVerifier.ChecksumSidecarSuffix;

        public static AriaBinaryFixture Create(string content)
        {
            var rootPath = Path.Combine(
                Path.GetTempPath(),
                $"downkyi-aria-integrity-{Guid.NewGuid():N}");
            Directory.CreateDirectory(rootPath);
            var executablePath = Path.Combine(rootPath, "aria2c-test");
            File.WriteAllText(executablePath, content);
            var hash = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(content)))
                .ToUpperInvariant();
            File.WriteAllText(
                executablePath + AriaBinaryIntegrityVerifier.ChecksumSidecarSuffix,
                hash);
            return new AriaBinaryFixture(rootPath, executablePath);
        }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }
}
