using System.Reflection;
using System.Runtime.Loader;
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
    public void DifferentInstallCopiesCannotOwnTheApplicationIdentityTogether()
    {
        var root = Path.Combine(Path.GetTempPath(), $"downkyi-guard-{Guid.NewGuid():N}");
        var firstDirectory = Path.Combine(root, "first-install");
        var secondDirectory = Path.Combine(root, "second-install");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);

        var assemblyName = Path.GetFileName(typeof(SingleInstanceGuard).Assembly.Location);
        var firstCopy = Path.Combine(firstDirectory, assemblyName);
        var secondCopy = Path.Combine(secondDirectory, assemblyName);
        File.Copy(typeof(SingleInstanceGuard).Assembly.Location, firstCopy);
        File.Copy(typeof(SingleInstanceGuard).Assembly.Location, secondCopy);

        var firstContext = new AssemblyLoadContext($"first-{Guid.NewGuid():N}", isCollectible: true);
        var secondContext = new AssemblyLoadContext($"second-{Guid.NewGuid():N}", isCollectible: true);
        try
        {
            var owner = $"owner-{Guid.NewGuid():N}";
            var first = TryAcquireFromCopy(firstContext, firstCopy, owner);
            Assert.True(first.Acquired);
            using (first.Guard)
            {
                var second = TryAcquireFromCopy(secondContext, secondCopy, owner);
                second.Guard?.Dispose();
                Assert.False(second.Acquired);
                Assert.Null(second.Guard);
            }
        }
        finally
        {
            firstContext.Unload();
            secondContext.Unload();
            Directory.Delete(root, recursive: true);
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

    private static (bool Acquired, IDisposable? Guard) TryAcquireFromCopy(
        AssemblyLoadContext context,
        string assemblyPath,
        string owner)
    {
        using var assemblyStream = File.OpenRead(assemblyPath);
        var assembly = context.LoadFromStream(assemblyStream);
        var guardType = assembly.GetType("DownKyi.Platform.SingleInstanceGuard", throwOnError: true)!;
        var tryAcquire = guardType.GetMethod("TryAcquire", BindingFlags.Public | BindingFlags.Static)!;
        object?[] arguments = [owner, "repo", null];
        var acquired = (bool)tryAcquire.Invoke(null, arguments)!;
        return (acquired, (IDisposable?)arguments[2]);
    }
}
