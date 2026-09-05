using System.Diagnostics;
using DownKyi.Core.Aria2cNet.Server;
using DownKyi.TestInfrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Core.Tests;

public sealed class AriaServerProcessTests
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
        var process = StartLongRunningProcess();

        await ExternalProcessTestHarness.RunWithCleanupAsync(
            async () =>
            {
                server.SetTrackedServerForTests(process);
                Assert.True(server.KillTrackedServer("test cleanup"));
                await process
                    .WaitForExitAsync(TestContext.Current.CancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)
                    .ConfigureAwait(true);

                Assert.True(process.HasExited);
                Assert.False(server.HasTrackedServerForTests());
            },
            () =>
            {
                server.SetTrackedServerForTests(null);
                return Task.CompletedTask;
            },
            () => ExternalProcessTestHarness.StopAsync(process, TimeSpan.FromSeconds(5)),
            () =>
            {
                process.Dispose();
                return Task.CompletedTask;
            });
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

        return ExternalProcessTestHarness.Start(startInfo);
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
