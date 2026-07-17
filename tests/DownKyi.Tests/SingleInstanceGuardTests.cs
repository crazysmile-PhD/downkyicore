using DownKyi.Platform;

namespace DownKyi.Tests;

public sealed class SingleInstanceGuardTests
{
    [Fact]
    public void MutexNameIsStablePerInstallWithoutLeakingThePath()
    {
        var firstPath = Path.Combine(Path.GetTempPath(), "DownKyi", "first-install");
        var secondPath = Path.Combine(Path.GetTempPath(), "DownKyi", "second-install");

        var first = SingleInstanceGuard.BuildMutexName("owner", "repo", firstPath);
        var repeated = SingleInstanceGuard.BuildMutexName("owner", "repo", firstPath);
        var second = SingleInstanceGuard.BuildMutexName("owner", "repo", secondPath);

        Assert.Equal(first, repeated);
        Assert.NotEqual(first, second);
        Assert.DoesNotContain("first-install", first, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DownKyi-owner-repo-", first, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyOneGuardCanOwnTheSameInstallIdentity()
    {
        var installPath = Path.Combine(Path.GetTempPath(), $"downkyi-guard-{Guid.NewGuid():N}");

        Assert.True(SingleInstanceGuard.TryAcquire("owner", "repo", installPath, out var first));
        using (first)
        {
            var secondAcquired = SingleInstanceGuard.TryAcquire("owner", "repo", installPath, out var second);
            second?.Dispose();
            Assert.False(secondAcquired);
            Assert.Null(second);
        }

        Assert.True(SingleInstanceGuard.TryAcquire("owner", "repo", installPath, out var reacquired));
        reacquired?.Dispose();
    }
}
