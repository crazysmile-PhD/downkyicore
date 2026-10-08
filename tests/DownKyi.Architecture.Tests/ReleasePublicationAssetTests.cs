using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DownKyi.CentralTestRunner;

namespace DownKyi.Architecture.Tests;

public sealed class ReleasePublicationAssetTests
{
    private const string Version = "9.8.7";
    private const string VerificationArchiveName = $"DownKyi-{Version}-verification.zip";
    private const UnixFileMode AppImageMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly (string PackageName, string ManifestName)[] PackageDefinitions =
    [
        ($"DownKyi-{Version}-1.win-x64.zip", "publish-manifest-win-x64.json"),
        ($"DownKyi-{Version}-1.win-x86.zip", "publish-manifest-win-x86.json"),
        ($"DownKyi-{Version}_linux_self-contained.x86_64.AppImage", "publish-manifest-linux-x64-AppImage.json"),
        ($"downkyi_{Version}_linux_self-contained_amd64.deb", "publish-manifest-linux-x64-deb.json"),
        ($"downkyi_{Version}_linux_self-contained.x86_64.rpm", "publish-manifest-linux-x64-rpm.json"),
        ($"DownKyi-{Version}_linux_self-contained.aarch64.AppImage", "publish-manifest-linux-arm64-AppImage.json"),
        ($"downkyi_{Version}_linux_self-contained_arm64.deb", "publish-manifest-linux-arm64-deb.json"),
        ($"DownKyi-{Version}-osx-x64.dmg", "publish-manifest-osx-x64.json"),
        ($"DownKyi-{Version}-osx-arm64.dmg", "publish-manifest-osx-arm64.json")
    ];

    [Fact]
    public async Task AssemblerPublishesNineUnchangedPackagesAndOneVerificationArchive()
    {
        await AssertAssemblerPublishesAsync("artifacts").ConfigureAwait(true);
    }

    [Fact]
    public async Task AssemblerTreatsBracketedOutputParentAsLiteralPath()
    {
        await AssertAssemblerPublishesAsync("out[1]").ConfigureAwait(true);
    }

    private static async Task AssertAssemblerPublishesAsync(string outputParentName)
    {
        var root = CreateTemporaryDirectory();
        var artifacts = Path.Combine(root, "artifacts");
        var outputParent = Path.Combine(root, outputParentName);
        var output = Path.Combine(outputParent, "release-assets");

        try
        {
            var originalFiles = WriteFixture(artifacts);
            Directory.CreateDirectory(outputParent);

            var exitCode = await RunAssemblerAsync(artifacts, output).ConfigureAwait(true);

            Assert.Equal(0, exitCode);
            AssertPublishedAssets(output, originalFiles);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData($"DownKyi-{Version}-1.win-x64.zip.sha256")]
    [InlineData("publish-manifest-win-x64.json")]
    public async Task AssemblerRejectsMissingVerificationFileWithoutPublishing(string missingFile)
    {
        var root = CreateTemporaryDirectory();
        var artifacts = Path.Combine(root, "artifacts");
        var output = Path.Combine(artifacts, "release-assets");

        try
        {
            WriteFixture(artifacts);
            File.Delete(Path.Combine(artifacts, missingFile));

            var exitCode = await RunAssemblerAsync(artifacts, output).ConfigureAwait(true);

            Assert.NotEqual(0, exitCode);
            Assert.Empty(Directory.EnumerateDirectories(artifacts));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AssemblerRejectsChangedPackageWithoutPublishing()
    {
        var root = CreateTemporaryDirectory();
        var artifacts = Path.Combine(root, "artifacts");
        var output = Path.Combine(artifacts, "release-assets");

        try
        {
            WriteFixture(artifacts);
            await File.AppendAllTextAsync(
                Path.Combine(artifacts, PackageDefinitions[0].PackageName),
                "changed after checksum",
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            var exitCode = await RunAssemblerAsync(artifacts, output).ConfigureAwait(true);

            Assert.NotEqual(0, exitCode);
            Assert.Empty(Directory.EnumerateDirectories(artifacts));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertPublishedAssets(string output, Dictionary<string, byte[]> originalFiles)
    {
        var expectedFiles = PackageDefinitions
            .Select(definition => definition.PackageName)
            .Append(VerificationArchiveName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var actualFiles = Directory.EnumerateFiles(output)
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedFiles, actualFiles);

        foreach (var definition in PackageDefinitions)
        {
            var path = Path.Combine(output, definition.PackageName);
            Assert.Equal(originalFiles[definition.PackageName], File.ReadAllBytes(path));
            if (!OperatingSystem.IsWindows() && path.EndsWith(".AppImage", StringComparison.Ordinal))
            {
                Assert.Equal(AppImageMode, File.GetUnixFileMode(path));
            }
        }

        using var archive = ZipFile.OpenRead(Path.Combine(output, VerificationArchiveName));
        var expectedEntries = PackageDefinitions
            .SelectMany(definition => new[] { $"{definition.PackageName}.sha256", definition.ManifestName })
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            expectedEntries,
            archive.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal).ToArray());
        foreach (var entry in archive.Entries)
        {
            using var stream = entry.Open();
            using var content = new MemoryStream();
            stream.CopyTo(content);
            Assert.Equal(originalFiles[entry.FullName], content.ToArray());
        }
    }

    private static Dictionary<string, byte[]> WriteFixture(string artifacts)
    {
        Directory.CreateDirectory(artifacts);
        foreach (var definition in PackageDefinitions)
        {
            var packagePath = Path.Combine(artifacts, definition.PackageName);
            File.WriteAllText(packagePath, $"package:{definition.PackageName}", Encoding.UTF8);
            if (!OperatingSystem.IsWindows() && packagePath.EndsWith(".AppImage", StringComparison.Ordinal))
            {
                File.SetUnixFileMode(packagePath, AppImageMode);
            }

            var packageHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(packagePath)));
            File.WriteAllText($"{packagePath}.sha256", $"{packageHash}  {definition.PackageName}\r\n", Encoding.ASCII);
            var manifest = "{\r\n" +
                $"  \"sourcePackage\": \"{definition.PackageName}\",\r\n" +
                $"  \"files\": [{{\"path\": \"DownKyi.fixture\", \"bytes\": 1, \"sha256\": \"{packageHash}\"}}],\r\n" +
                "  \"extra\": \"保留原始內容、欄位與換行\"\r\n}\r\n";
            File.WriteAllText(
                Path.Combine(artifacts, definition.ManifestName),
                manifest,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }

        return Directory.EnumerateFiles(artifacts)
            .ToDictionary(path => Path.GetFileName(path), File.ReadAllBytes, StringComparer.Ordinal);
    }

    private static async Task<int> RunAssemblerAsync(string artifacts, string output)
    {
        var startInfo = new ProcessStartInfo("pwsh") { UseShellExecute = false };
        string[] arguments =
        [
            "-NoLogo", "-NoProfile", "-NonInteractive", "-File",
            Path.Combine(RepositoryRoot, "script", "assemble-release-assets.ps1"),
            "-ArtifactsDirectory", artifacts, "-ExpectedVersion", Version, "-OutputDirectory", output
        ];
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMinutes(1));
        return await BuildProcessRunner.RunAsync(startInfo, cancellation.Token).ConfigureAwait(true);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"downkyi-release-publication-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DownKyi.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
