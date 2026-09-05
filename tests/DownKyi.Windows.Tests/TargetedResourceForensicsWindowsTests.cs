using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using DownKyi.TestInfrastructure;

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
            forensics = TargetedResourceForensics.Start(
                targetDirectory,
                nameof(ControlledDirectoryOwnerProducesCorrelatedLifecycleArtifact),
                Environment.ProcessId);
            owner = StartDirectoryOwner(targetDirectory);
            var ready = await owner.StandardOutput.ReadLineAsync(
                    TestContext.Current.CancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            Assert.StartsWith("fixture-ready pid=", ready, StringComparison.Ordinal);
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
}
