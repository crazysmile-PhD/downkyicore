using DownKyi.Application.Downloads;
using DownKyi.Infrastructure.Downloads;

namespace DownKyi.Infrastructure.Tests;

public sealed class FileSystemPhysicalOutputPathResolverTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "downkyi-physical-output-path-tests",
        Guid.NewGuid().ToString("N"));
    private readonly FileSystemPhysicalOutputPathResolver _resolver = new();

    [Fact]
    public void ContractAndAdapterRespectLayerOwnership()
    {
        Assert.Equal(
            "DownKyi.Application",
            typeof(IPhysicalOutputPathResolver).Assembly.GetName().Name);
        Assert.Equal(
            "DownKyi.Infrastructure",
            typeof(FileSystemPhysicalOutputPathResolver).Assembly.GetName().Name);
        Assert.Null(typeof(FileSystemPhysicalOutputPathResolver).GetProperty("Instance"));
    }

    [Fact]
    public void ContractResolvesOnePathWithoutReservationPolicy()
    {
        var method = Assert.Single(typeof(IPhysicalOutputPathResolver).GetMethods());

        Assert.Equal("ResolvePhysicalBasePath", method.Name);
        Assert.Equal(
            ["logicalBasePath"],
            method.GetParameters().Select(parameter => parameter.Name));
    }

    [Fact]
    public void CanonicalUnicodeSpellingsRemainDistinctPhysicalPaths()
    {
        var composed = Path.Combine(_directory, "caf\u00E9", "video");
        var decomposed = Path.Combine(_directory, "cafe\u0301", "video");

        Assert.NotEqual(
            _resolver.ResolvePhysicalBasePath(composed),
            _resolver.ResolvePhysicalBasePath(decomposed));
    }

    [Fact]
    public void TraversalFailureDoesNotExposeAbsolutePath()
    {
        Directory.CreateDirectory(_directory);
        var blocker = Path.Combine(_directory, "private-file");
        File.WriteAllText(blocker, "not a directory");
        var outputStem = Path.Combine(blocker, "nested", "video");

        var exception = Assert.Throws<IOException>(() =>
            _resolver.ResolvePhysicalBasePath(outputStem));

        Assert.Equal("Unable to resolve physical output path.", exception.Message);
        Assert.DoesNotContain(_directory, exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
