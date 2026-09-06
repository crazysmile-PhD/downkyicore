using DownKyi.Infrastructure.Downloads;

namespace DownKyi.Windows.Tests;

public sealed class FileSystemPhysicalOutputPathResolverTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "downkyi-physical-output-path-windows-tests",
        Guid.NewGuid().ToString("N"));
    private readonly List<string> _aliases = [];
    private readonly FileSystemPhysicalOutputPathResolver _resolver = new();

    [Fact]
    public async Task JunctionAnchorsNonexistentSuffixToPhysicalParent()
    {
        var target = CreateDirectory("real");
        var alias = Path.Combine(_directory, "alias");
        await CreateJunctionAsync(alias, target).ConfigureAwait(true);

        AssertSamePath(
            Path.Combine(alias, "future", "nested", "video"),
            Path.Combine(target, "future", "nested", "video"));
    }

    [Fact]
    public async Task JunctionAfterCanceledMissingComponentIsResolved()
    {
        var target = CreateDirectory("canceled-missing-target");
        var alias = Path.Combine(_directory, "canceled-missing-alias");
        await CreateJunctionAsync(alias, target).ConfigureAwait(true);

        AssertSamePath(
            Path.Combine(_directory, "missing", "..", "canceled-missing-alias", "video"),
            Path.Combine(target, "video"));
    }

    [Fact]
    public async Task CanonicallyDistinctAliasNameUsesRawTraversalState()
    {
        var decomposedTarget = CreateDirectory("cafe\u0301");
        var composedAlias = Path.Combine(_directory, "caf\u00E9");
        await CreateJunctionAsync(composedAlias, decomposedTarget).ConfigureAwait(true);

        AssertSamePath(
            Path.Combine(composedAlias, "video"),
            Path.Combine(decomposedTarget, "video"));
    }

    [Fact]
    public async Task DanglingJunctionUsesLinkMetadata()
    {
        Directory.CreateDirectory(_directory);
        var target = Path.Combine(_directory, "future-target");
        var alias = Path.Combine(_directory, "dangling");
        await CreateJunctionAsync(alias, target).ConfigureAwait(true);

        AssertSamePath(Path.Combine(alias, "video"), Path.Combine(target, "video"));
    }

    [Fact]
    public async Task JunctionRetargetIsObservedAcrossResolves()
    {
        var firstTarget = CreateDirectory("first");
        var secondTarget = CreateDirectory("second");
        var alias = Path.Combine(_directory, "alias");
        await CreateJunctionAsync(alias, firstTarget).ConfigureAwait(true);
        var first = _resolver.ResolvePhysicalBasePath(Path.Combine(alias, "video"));

        DeleteAlias(alias);
        await CreateJunctionAsync(alias, secondTarget).ConfigureAwait(true);
        var second = _resolver.ResolvePhysicalBasePath(Path.Combine(alias, "video"));

        Assert.NotEqual(first, second);
        Assert.Equal(
            _resolver.ResolvePhysicalBasePath(Path.Combine(secondTarget, "video")),
            second);
    }

    [Fact]
    public async Task FinalStemJunctionIsNotResolved()
    {
        var target = CreateDirectory("stem-target");
        var alias = Path.Combine(_directory, "video");
        await CreateJunctionAsync(alias, target).ConfigureAwait(true);

        var resolved = _resolver.ResolvePhysicalBasePath(alias);

        Assert.Equal(alias, resolved);
        Assert.NotEqual(_resolver.ResolvePhysicalBasePath(target), resolved);
    }

    [Fact]
    public async Task ReenteredAliasWithDifferentPendingSuffixIsNotCycle()
    {
        var target = CreateDirectory(Path.Combine("physical", "target"));
        var physicalRoot = Path.GetDirectoryName(target)!;
        var entry = Path.Combine(_directory, "logical");
        await CreateJunctionAsync(entry, physicalRoot).ConfigureAwait(true);
        var reentry = Path.Combine(physicalRoot, "reenter");
        await CreateJunctionAsync(reentry, Path.Combine(entry, "target")).ConfigureAwait(true);

        AssertSamePath(Path.Combine(entry, "reenter", "video"), Path.Combine(target, "video"));
    }

    [Fact]
    public async Task JunctionCycleFailsClosedWithoutPath()
    {
        Directory.CreateDirectory(_directory);
        var first = Path.Combine(_directory, "cycle-first");
        var second = Path.Combine(_directory, "cycle-second");
        await CreateJunctionAsync(first, second).ConfigureAwait(true);
        await CreateJunctionAsync(second, first).ConfigureAwait(true);

        var exception = Assert.Throws<IOException>(() =>
            _resolver.ResolvePhysicalBasePath(Path.Combine(first, "video")));

        Assert.Equal("Unable to resolve physical output path.", exception.Message);
        Assert.DoesNotContain(_directory, exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExcessiveJunctionDepthFailsClosedWithoutPath()
    {
        Directory.CreateDirectory(_directory);
        for (var index = 0; index <= 40; index++)
        {
            await CreateJunctionAsync(
                Path.Combine(_directory, $"link-{index}"),
                Path.Combine(_directory, $"link-{index + 1}")).ConfigureAwait(true);
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
            Directory.Delete(alias);
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

    private void AssertSamePath(string logicalPath, string physicalPath) =>
        Assert.Equal(
            _resolver.ResolvePhysicalBasePath(physicalPath),
            _resolver.ResolvePhysicalBasePath(logicalPath));

    private async Task CreateJunctionAsync(string alias, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(alias)!);
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(alias);
        startInfo.ArgumentList.Add(target);

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the junction helper.");
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new IOException($"Failed to create the test junction: {error}");
        }

        _aliases.Add(alias);
    }

    private void DeleteAlias(string alias)
    {
        Directory.Delete(alias);
        _aliases.Remove(alias);
    }
}
