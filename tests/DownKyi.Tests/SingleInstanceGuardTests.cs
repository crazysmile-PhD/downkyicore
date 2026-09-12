using System.Diagnostics;
using DownKyi.Platform;

namespace DownKyi.Tests;

public sealed class SingleInstanceGuardTests
{
    [Fact]
    public void MutexNameUsesStableApplicationIdentityWithoutAnInstallPath()
    {
        var first = SingleInstanceGuard.BuildMutexName("owner", "repo");
        var repeated = SingleInstanceGuard.BuildMutexName("owner", "repo");

        Assert.Equal("DownKyi-owner-repo", first);
        Assert.Equal(first, repeated);
        Assert.DoesNotContain(Path.GetTempPath(), first, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OnlyOneGuardCanOwnTheSameApplicationIdentity()
    {
        var owner = $"owner-{Guid.NewGuid():N}";

        Assert.True(SingleInstanceGuard.TryAcquire(owner, "repo", out var first));
        using (first)
        {
            var secondAcquired = SingleInstanceGuard.TryAcquire(owner, "repo", out var second);
            second?.Dispose();
            Assert.False(secondAcquired);
            Assert.Null(second);
        }
    }

    [Fact]
    public async Task DifferentInstallCopiesCannotOwnTheApplicationIdentityTogether()
    {
        var root = Path.Combine(Path.GetTempPath(), $"downkyi-guard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var sourceDirectory = AppContext.BaseDirectory;
            foreach (var source in Directory.EnumerateFiles(sourceDirectory))
            {
                File.Copy(source, Path.Combine(root, Path.GetFileName(source)));
            }

            var owner = $"owner-{Guid.NewGuid():N}";
            Assert.True(SingleInstanceGuard.TryAcquire(owner, "repo", out var first));
            using (first)
            {
                await AssertProbeResultAsync(root, owner, expectedAcquired: false);
            }

            await AssertProbeResultAsync(root, owner, expectedAcquired: true);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(Explicit = true)]
    public void ProbeApplicationIdentityInSeparateProcess()
    {
        var owner = Environment.GetEnvironmentVariable("DOWNKYI_SINGLE_INSTANCE_PROBE_OWNER");
        var expected = Environment.GetEnvironmentVariable("DOWNKYI_SINGLE_INSTANCE_PROBE_EXPECTED");
        Assert.False(string.IsNullOrWhiteSpace(owner));
        Assert.True(expected is "true" or "false");

        var acquired = SingleInstanceGuard.TryAcquire(owner, "repo", out var guard);
        using (guard)
        {
            Assert.Equal(expected == "true", acquired);
        }
    }

    [Fact]
    public void ReleasedApplicationIdentityCanBeAcquiredAgain()
    {
        var owner = $"owner-{Guid.NewGuid():N}";

        Assert.True(SingleInstanceGuard.TryAcquire(owner, "repo", out var first));
        first?.Dispose();

        Assert.True(SingleInstanceGuard.TryAcquire(owner, "repo", out var second));
        second?.Dispose();
    }

    private static async Task AssertProbeResultAsync(string directory, string owner, bool expectedAcquired)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(Path.Combine(
            directory, Path.GetFileName(typeof(SingleInstanceGuardTests).Assembly.Location)));
        startInfo.ArgumentList.Add("-explicit");
        startInfo.ArgumentList.Add("only");
        startInfo.ArgumentList.Add("-method");
        startInfo.ArgumentList.Add(
            $"{typeof(SingleInstanceGuardTests).FullName}.{nameof(ProbeApplicationIdentityInSeparateProcess)}");
        startInfo.Environment["DOWNKYI_SINGLE_INSTANCE_PROBE_OWNER"] = owner;
        startInfo.Environment["DOWNKYI_SINGLE_INSTANCE_PROBE_EXPECTED"] =
            expectedAcquired ? "true" : "false";

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            }

            throw;
        }

        Assert.True(process.ExitCode == 0,
            $"Second installation probe exited {process.ExitCode}. " +
            $"{await output.ConfigureAwait(true)} {await error.ConfigureAwait(true)}");
    }
}
