using DownKyi.Infrastructure.Downloads;

namespace DownKyi.MacOS.Tests;

public sealed class FileSystemPhysicalOutputPathResolverMacAliasTests
{
    [Fact]
    public void VarAndPrivateVarResolveToTheSamePhysicalOutputBase()
    {
        var resolver = new FileSystemPhysicalOutputPathResolver();
        var suffix = Path.Combine("tmp", $"downkyi-{Guid.NewGuid():N}", "video");
        var logicalBasePath = Path.Combine("/var", suffix);
        var physicalBasePath = Path.Combine("/private/var", suffix);

        Assert.Equal(
            resolver.ResolvePhysicalBasePath(physicalBasePath),
            resolver.ResolvePhysicalBasePath(logicalBasePath));
    }
}
