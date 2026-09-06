using DownKyi.Infrastructure.Downloads;

namespace DownKyi.Platform.Tests;

public sealed class FileSystemPhysicalOutputPathResolverUnixTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "downkyi-physical-output-path-unix-tests",
        Guid.NewGuid().ToString("N"));
    private readonly List<string> _aliases = [];
    private readonly FileSystemPhysicalOutputPathResolver _resolver = new();

    [Fact]
    public void SymlinkAnchorsNonexistentSuffixToPhysicalParent()
    {
        var target = CreateDirectory("real");
        var alias = CreateSymlink("alias", target);

        AssertSamePath(
            Path.Combine(alias, "future", "nested", "video"),
            Path.Combine(target, "future", "nested", "video"));
    }

    [Fact]
    public void CanonicallyDistinctEntriesUseRawTraversalState()
    {
        var decomposedTarget = CreateDirectory("cafe\u0301");
        var composedAlias = CreateSymlink("caf\u00E9", decomposedTarget);

        AssertSamePath(
            Path.Combine(composedAlias, "video"),
            Path.Combine(decomposedTarget, "video"));
    }

    [Fact]
    public void RelativeTargetResolvesLinkSegmentBeforeDotDot()
    {
        var target = CreateDirectory(Path.Combine("other", "real"));
        var middleTarget = CreateDirectory(Path.Combine("other", "child"));
        CreateSymlink("middle", middleTarget);
        var outer = CreateSymlink("outer", Path.Combine("middle", "..", "real"));

        AssertSamePath(Path.Combine(outer, "video"), Path.Combine(target, "video"));
    }

    [Fact]
    public void DanglingSymlinkUsesLinkMetadata()
    {
        Directory.CreateDirectory(_directory);
        var target = Path.Combine(_directory, "future-target");
        var alias = CreateSymlink("dangling", target);

        AssertSamePath(Path.Combine(alias, "video"), Path.Combine(target, "video"));
    }

    [Fact]
    public void SymlinkRetargetIsObservedAcrossResolves()
    {
        var firstTarget = CreateDirectory("first");
        var secondTarget = CreateDirectory("second");
        var alias = CreateSymlink("alias", firstTarget);
        var first = _resolver.ResolvePhysicalBasePath(Path.Combine(alias, "video"));

        DeleteAlias(alias);
        alias = CreateSymlink("alias", secondTarget);
        var second = _resolver.ResolvePhysicalBasePath(Path.Combine(alias, "video"));

        Assert.NotEqual(first, second);
        Assert.Equal(
            _resolver.ResolvePhysicalBasePath(Path.Combine(secondTarget, "video")),
            second);
    }

    [Fact]
    public void FinalStemSymlinkIsNotResolved()
    {
        var target = CreateDirectory("stem-target");
        var alias = CreateSymlink("video", target);

        var resolved = _resolver.ResolvePhysicalBasePath(alias);

        Assert.Equal(alias, resolved);
        Assert.NotEqual(_resolver.ResolvePhysicalBasePath(target), resolved);
    }

    [Fact]
    public void VarStyleReentryWithDifferentPendingSuffixIsNotCycle()
    {
        var target = CreateDirectory(Path.Combine("var", "physical", "target"));
        var physicalRoot = Path.GetDirectoryName(target)!;
        var entry = CreateSymlink(Path.Combine("var", "logical"), physicalRoot);
        CreateSymlink(
            Path.Combine("var", "physical", "reenter"),
            Path.Combine(entry, "target"));

        AssertSamePath(Path.Combine(entry, "reenter", "video"), Path.Combine(target, "video"));
    }

    [Fact]
    public void SymlinkCycleFailsClosedWithoutPath()
    {
        Directory.CreateDirectory(_directory);
        var first = CreateSymlink("cycle-first", Path.Combine(_directory, "cycle-second"));
        CreateSymlink("cycle-second", first);

        var exception = Assert.Throws<IOException>(() =>
            _resolver.ResolvePhysicalBasePath(Path.Combine(first, "video")));

        Assert.Equal("Unable to resolve physical output path.", exception.Message);
        Assert.DoesNotContain(_directory, exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExcessiveSymlinkDepthFailsClosedWithoutPath()
    {
        Directory.CreateDirectory(_directory);
        for (var index = 0; index <= 40; index++)
        {
            CreateSymlink($"link-{index}", Path.Combine(_directory, $"link-{index + 1}"));
        }

        var exception = Assert.Throws<IOException>(() =>
            _resolver.ResolvePhysicalBasePath(Path.Combine(_directory, "link-0", "video")));

        Assert.Equal("Unable to resolve physical output path.", exception.Message);
        Assert.DoesNotContain(_directory, exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        foreach (var alias in _aliases.Distinct(StringComparer.Ordinal).Reverse())
        {
            File.Delete(alias);
        }

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string CreateDirectory(string relativePath)
    {
        var path = Path.Combine(_directory, relativePath);
        Directory.CreateDirectory(path);
        return path;
    }

    private string CreateSymlink(string relativeAlias, string target)
    {
        var alias = Path.Combine(_directory, relativeAlias);
        Directory.CreateDirectory(Path.GetDirectoryName(alias)!);
        Directory.CreateSymbolicLink(alias, target);
        _aliases.Add(alias);
        return alias;
    }

    private void DeleteAlias(string alias)
    {
        File.Delete(alias);
        _aliases.Remove(alias);
    }

    private void AssertSamePath(string logicalPath, string physicalPath) =>
        Assert.Equal(
            _resolver.ResolvePhysicalBasePath(physicalPath),
            _resolver.ResolvePhysicalBasePath(logicalPath));
}
