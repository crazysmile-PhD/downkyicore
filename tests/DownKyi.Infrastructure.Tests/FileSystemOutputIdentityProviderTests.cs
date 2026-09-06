using DownKyi.Application.Downloads;
using DownKyi.Infrastructure.Downloads;

namespace DownKyi.Infrastructure.Tests;

public sealed class FileSystemOutputIdentityProviderTests : IDisposable
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    public static bool IsUnix => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "downkyi-output-identity-tests",
        Guid.NewGuid().ToString("N"));
    private readonly List<string> _aliases = [];
    private readonly FileSystemOutputIdentityProvider _provider = new();

    [Fact]
    public void ContractAndAdapterRespectLayerOwnership()
    {
        Assert.Equal("DownKyi.Application", typeof(IOutputIdentityProvider).Assembly.GetName().Name);
        Assert.Equal(
            "DownKyi.Infrastructure",
            typeof(FileSystemOutputIdentityProvider).Assembly.GetName().Name);
        Assert.Null(typeof(FileSystemOutputIdentityProvider).GetProperty("Instance"));
    }

    [Fact]
    public void CanonicalUnicodeVariantsShareIdentityBeforeCasePolicy()
    {
        var composed = Path.Combine(_directory, "caf\u00E9");
        var decomposed = Path.Combine(_directory, "cafe\u0301");

        Assert.Equal(
            _provider.CreateReservationKey(composed, ignoreCase: false),
            _provider.CreateReservationKey(decomposed, ignoreCase: false));
        Assert.Equal(
            _provider.CreateReservationKey(composed, ignoreCase: true),
            _provider.CreateReservationKey(decomposed.ToUpperInvariant(), ignoreCase: true));
    }

    [Fact]
    public void TraversalFailureDoesNotExposeAbsolutePath()
    {
        Directory.CreateDirectory(_directory);
        var blocker = Path.Combine(_directory, "private-file");
        File.WriteAllText(blocker, "not a directory");
        var outputStem = Path.Combine(blocker, "nested", "video");

        var exception = Assert.Throws<IOException>(() =>
            _provider.CreateReservationKey(outputStem, ignoreCase: false));

        Assert.Equal("Unable to resolve output filesystem identity.", exception.Message);
        Assert.DoesNotContain(_directory, exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact(Skip = "Requires Windows directory-junction semantics.", SkipUnless = nameof(IsWindows))]
    public async Task WindowsJunctionAnchorsNonexistentSuffixToPhysicalParent()
    {
        var realDirectory = CreateDirectory("windows-real");
        var alias = Path.Combine(_directory, "windows-alias");
        await CreateWindowsJunctionAsync(alias, realDirectory).ConfigureAwait(true);

        AssertAliasesShareIdentity(
            Path.Combine(alias, "future", "nested", "video"),
            Path.Combine(realDirectory, "future", "nested", "video"));
    }

    [Fact(Skip = "Requires Windows directory-junction semantics.", SkipUnless = nameof(IsWindows))]
    public async Task WindowsDanglingJunctionUsesLinkMetadata()
    {
        Directory.CreateDirectory(_directory);
        var futureTarget = Path.Combine(_directory, "future-target");
        var alias = Path.Combine(_directory, "dangling-junction");
        await CreateWindowsJunctionAsync(alias, futureTarget).ConfigureAwait(true);

        AssertAliasesShareIdentity(
            Path.Combine(alias, "video"),
            Path.Combine(futureTarget, "video"));
    }

    [Fact(Skip = "Requires Windows directory-junction semantics.", SkipUnless = nameof(IsWindows))]
    public async Task WindowsJunctionRetargetIsObserved()
    {
        var firstTarget = CreateDirectory("retarget-first");
        var secondTarget = CreateDirectory("retarget-second");
        var alias = Path.Combine(_directory, "retarget-junction");
        await CreateWindowsJunctionAsync(alias, firstTarget).ConfigureAwait(true);
        var firstKey = _provider.CreateReservationKey(
            Path.Combine(alias, "video"),
            ignoreCase: false);

        DeleteAlias(alias);
        await CreateWindowsJunctionAsync(alias, secondTarget).ConfigureAwait(true);
        var secondKey = _provider.CreateReservationKey(
            Path.Combine(alias, "video"),
            ignoreCase: false);

        Assert.NotEqual(firstKey, secondKey);
        Assert.Equal(
            _provider.CreateReservationKey(Path.Combine(secondTarget, "video"), ignoreCase: false),
            secondKey);
    }

    [Fact(Skip = "Requires Windows directory-junction semantics.", SkipUnless = nameof(IsWindows))]
    public async Task WindowsStemJunctionIsNotResolved()
    {
        var target = CreateDirectory("stem-target");
        var stemAlias = Path.Combine(_directory, "video");
        await CreateWindowsJunctionAsync(stemAlias, target).ConfigureAwait(true);

        AssertStemAliasIsNotResolved(stemAlias, target);
    }

    [Fact(Skip = "Requires Windows directory-junction semantics.", SkipUnless = nameof(IsWindows))]
    public async Task WindowsNestedJunctionTargetsAreFullyResolved()
    {
        var realDirectory = CreateDirectory(Path.Combine("nested-real", "child"));
        var middleAlias = Path.Combine(_directory, "nested-middle");
        await CreateWindowsJunctionAsync(
            middleAlias,
            Path.GetDirectoryName(realDirectory)!).ConfigureAwait(true);
        var outerAlias = Path.Combine(_directory, "nested-outer");
        await CreateWindowsJunctionAsync(
            outerAlias,
            Path.Combine(middleAlias, "child")).ConfigureAwait(true);

        AssertAliasesShareIdentity(
            Path.Combine(outerAlias, "video"),
            Path.Combine(realDirectory, "video"));
    }

    [Fact(Skip = "Requires Windows directory-junction semantics.", SkipUnless = nameof(IsWindows))]
    public async Task WindowsRepeatedAncestorAliasWithDifferentPendingSuffixIsNotCycle()
    {
        var physicalTarget = CreateDirectory(Path.Combine("reentry-physical", "target"));
        var physicalRoot = Path.GetDirectoryName(physicalTarget)!;
        var entryAlias = Path.Combine(_directory, "reentry-logical");
        await CreateWindowsJunctionAsync(entryAlias, physicalRoot).ConfigureAwait(true);
        var innerAlias = Path.Combine(physicalRoot, "reenter");
        await CreateWindowsJunctionAsync(
            innerAlias,
            Path.Combine(entryAlias, "target")).ConfigureAwait(true);

        AssertAliasesShareIdentity(
            Path.Combine(entryAlias, "reenter", "video"),
            Path.Combine(physicalTarget, "video"));
    }

    [Fact(Skip = "Requires Windows directory-junction semantics.", SkipUnless = nameof(IsWindows))]
    public async Task WindowsJunctionCycleFailsClosedWithoutPath()
    {
        Directory.CreateDirectory(_directory);
        var first = Path.Combine(_directory, "cycle-first");
        var second = Path.Combine(_directory, "cycle-second");
        await CreateWindowsJunctionAsync(first, second).ConfigureAwait(true);
        await CreateWindowsJunctionAsync(second, first).ConfigureAwait(true);

        AssertTopologyFailureIsRedacted(Path.Combine(first, "video"));
    }

    [Fact(Skip = "Requires Unix directory-symlink semantics.", SkipUnless = nameof(IsUnix))]
    public void UnixSymlinkAnchorsNonexistentSuffixToPhysicalParent()
    {
        var realDirectory = CreateDirectory("unix-real");
        var alias = Path.Combine(_directory, "cafe\u0301-alias");
        CreateUnixDirectorySymlink(alias, realDirectory);

        AssertAliasesShareIdentity(
            Path.Combine(alias, "future", "nested", "video"),
            Path.Combine(realDirectory, "future", "nested", "video"));
    }

    [Fact(Skip = "Requires Unix directory-symlink semantics.", SkipUnless = nameof(IsUnix))]
    public void UnixDanglingSymlinkUsesLinkMetadata()
    {
        Directory.CreateDirectory(_directory);
        var futureTarget = Path.Combine(_directory, "future-target");
        var alias = Path.Combine(_directory, "dangling-symlink");
        CreateUnixDirectorySymlink(alias, futureTarget);

        AssertAliasesShareIdentity(
            Path.Combine(alias, "video"),
            Path.Combine(futureTarget, "video"));
    }

    [Fact(Skip = "Requires Unix directory-symlink semantics.", SkipUnless = nameof(IsUnix))]
    public void UnixSymlinkRetargetIsObserved()
    {
        var firstTarget = CreateDirectory("retarget-first");
        var secondTarget = CreateDirectory("retarget-second");
        var alias = Path.Combine(_directory, "retarget-symlink");
        CreateUnixDirectorySymlink(alias, firstTarget);
        var firstKey = _provider.CreateReservationKey(
            Path.Combine(alias, "video"),
            ignoreCase: false);

        DeleteAlias(alias);
        CreateUnixDirectorySymlink(alias, secondTarget);
        var secondKey = _provider.CreateReservationKey(
            Path.Combine(alias, "video"),
            ignoreCase: false);

        Assert.NotEqual(firstKey, secondKey);
        Assert.Equal(
            _provider.CreateReservationKey(Path.Combine(secondTarget, "video"), ignoreCase: false),
            secondKey);
    }

    [Fact(Skip = "Requires Unix directory-symlink semantics.", SkipUnless = nameof(IsUnix))]
    public void UnixStemSymlinkIsNotResolved()
    {
        var target = CreateDirectory("stem-target");
        var stemAlias = Path.Combine(_directory, "video");
        CreateUnixDirectorySymlink(stemAlias, target);

        AssertStemAliasIsNotResolved(stemAlias, target);
    }

    [Fact(Skip = "Requires Unix directory-symlink semantics.", SkipUnless = nameof(IsUnix))]
    public void UnixNestedSymlinkTargetsAreFullyResolved()
    {
        var realDirectory = CreateDirectory(Path.Combine("nested-real", "child"));
        var middleAlias = Path.Combine(_directory, "nested-middle");
        CreateUnixDirectorySymlink(middleAlias, Path.GetDirectoryName(realDirectory)!);
        var outerAlias = Path.Combine(_directory, "nested-outer");
        CreateUnixDirectorySymlink(outerAlias, Path.Combine(middleAlias, "child"));

        AssertAliasesShareIdentity(
            Path.Combine(outerAlias, "video"),
            Path.Combine(realDirectory, "video"));
    }

    [Fact(Skip = "Requires Unix directory-symlink semantics.", SkipUnless = nameof(IsUnix))]
    public void UnixRepeatedAncestorAliasWithDifferentPendingSuffixIsNotCycle()
    {
        var physicalTarget = CreateDirectory(Path.Combine("reentry-physical", "target"));
        var physicalRoot = Path.GetDirectoryName(physicalTarget)!;
        var entryAlias = Path.Combine(_directory, "reentry-logical");
        CreateUnixDirectorySymlink(entryAlias, physicalRoot);
        var innerAlias = Path.Combine(physicalRoot, "reenter");
        CreateUnixDirectorySymlink(innerAlias, Path.Combine(entryAlias, "target"));

        AssertAliasesShareIdentity(
            Path.Combine(entryAlias, "reenter", "video"),
            Path.Combine(physicalTarget, "video"));
    }

    [Fact(Skip = "Requires Unix directory-symlink semantics.", SkipUnless = nameof(IsUnix))]
    public void UnixSymlinkCycleFailsClosedWithoutPath()
    {
        Directory.CreateDirectory(_directory);
        var first = Path.Combine(_directory, "cycle-first");
        var second = Path.Combine(_directory, "cycle-second");
        CreateUnixDirectorySymlink(first, second);
        CreateUnixDirectorySymlink(second, first);

        AssertTopologyFailureIsRedacted(Path.Combine(first, "video"));
    }

    public void Dispose()
    {
        foreach (var alias in _aliases.Distinct(StringComparer.Ordinal).Reverse())
        {
            DeleteOwnedAlias(alias);
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

    private void AssertAliasesShareIdentity(string aliasPath, string directPath)
    {
        Assert.Equal(
            _provider.CreateReservationKey(directPath, ignoreCase: false),
            _provider.CreateReservationKey(aliasPath, ignoreCase: false));
    }

    private void AssertStemAliasIsNotResolved(string stemAlias, string target)
    {
        var stemKey = _provider.CreateReservationKey(stemAlias, ignoreCase: false);
        var siblingKey = _provider.CreateReservationKey(
            Path.Combine(Path.GetDirectoryName(stemAlias)!, "ordinary-sibling"),
            ignoreCase: false);

        Assert.Equal(Path.GetDirectoryName(siblingKey), Path.GetDirectoryName(stemKey));
        Assert.Equal(Path.GetFileName(stemAlias), Path.GetFileName(stemKey));
        Assert.NotEqual(
            _provider.CreateReservationKey(target, ignoreCase: false),
            stemKey);
    }

    private void AssertTopologyFailureIsRedacted(string outputStem)
    {
        var exception = Assert.Throws<IOException>(() =>
            _provider.CreateReservationKey(outputStem, ignoreCase: false));

        Assert.Equal("Unable to resolve output filesystem identity.", exception.Message);
        Assert.DoesNotContain(_directory, exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private async Task CreateWindowsJunctionAsync(string alias, string target)
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
        var standardOutput = process.StandardOutput.ReadToEndAsync(
            TestContext.Current.CancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(
            TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        await standardOutput.ConfigureAwait(false);
        var error = await standardError.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new IOException($"Failed to create the test junction: {error}");
        }

        _aliases.Add(alias);
    }

    private void CreateUnixDirectorySymlink(string alias, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(alias)!);
        Directory.CreateSymbolicLink(alias, target);
        _aliases.Add(alias);
    }

    private void DeleteAlias(string alias)
    {
        DeleteOwnedAlias(alias);
        _aliases.Remove(alias);
    }

    private static void DeleteOwnedAlias(string alias)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.Delete(alias);
            return;
        }

        File.Delete(alias);
    }
}
