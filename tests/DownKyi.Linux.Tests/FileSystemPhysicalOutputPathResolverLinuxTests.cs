using DownKyi.Infrastructure.Downloads;

namespace DownKyi.Linux.Tests;

public sealed class FileSystemPhysicalOutputPathResolverLinuxTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "downkyi-physical-output-path-linux-tests",
        Guid.NewGuid().ToString("N"));
    private string? _alias;

    [Fact]
    public void CanonicallyDistinctEntriesUseRawTraversalState()
    {
        var decomposedTarget = Path.Combine(_directory, "cafe\u0301");
        Directory.CreateDirectory(decomposedTarget);
        _alias = Path.Combine(_directory, "caf\u00E9");
        Directory.CreateSymbolicLink(_alias, decomposedTarget);
        var resolver = new FileSystemPhysicalOutputPathResolver();

        Assert.Equal(
            resolver.ResolvePhysicalBasePath(Path.Combine(decomposedTarget, "video")),
            resolver.ResolvePhysicalBasePath(Path.Combine(_alias, "video")));
    }

    public void Dispose()
    {
        if (_alias != null)
        {
            File.Delete(_alias);
        }

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
