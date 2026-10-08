using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DownKyi.Architecture.Tests;

public sealed class ReleasePublicationAssetTests
{
    private const string Version = "9.8.7";
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly PackageDefinition[] PackageDefinitions =
    [
        new($"DownKyi-{Version}-1.win-x64.zip", "publish-manifest-win-x64.json", "win-x64"),
        new($"DownKyi-{Version}-1.win-x86.zip", "publish-manifest-win-x86.json", "win-x86"),
        new($"DownKyi-{Version}_linux_self-contained.x86_64.AppImage", "publish-manifest-linux-x64-AppImage.json", "linux-x64"),
        new($"downkyi_{Version}_linux_self-contained_amd64.deb", "publish-manifest-linux-x64-deb.json", "linux-x64"),
        new($"downkyi_{Version}_linux_self-contained.x86_64.rpm", "publish-manifest-linux-x64-rpm.json", "linux-x64"),
        new($"DownKyi-{Version}_linux_self-contained.aarch64.AppImage", "publish-manifest-linux-arm64-AppImage.json", "linux-arm64"),
        new($"downkyi_{Version}_linux_self-contained_arm64.deb", "publish-manifest-linux-arm64-deb.json", "linux-arm64"),
        new($"DownKyi-{Version}-osx-x64.dmg", "publish-manifest-osx-x64.json", "osx-x64"),
        new($"DownKyi-{Version}-osx-arm64.dmg", "publish-manifest-osx-arm64.json", "osx-arm64")
    ];

    [Fact]
    public void AssemblerPublishesNinePackagesAndTwoConsolidatedEvidenceFiles()
    {
        var root = CreateTemporaryDirectory();
        var artifacts = Path.Combine(root, "downloaded-artifacts");
        var output = Path.Combine(root, "release-assets");

        try
        {
            WriteFixture(artifacts);

            var result = RunAssembler(artifacts, output);

            Assert.Equal(0, result.ExitCode);
            var outputFiles = Directory
                .EnumerateFiles(output)
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var expectedFiles = PackageDefinitions
                .Select(definition => definition.PackageName)
                .Append("SHA256SUMS.txt")
                .Append("release-manifest.json")
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(expectedFiles, outputFiles);

            var checksumLines = File
                .ReadAllLines(Path.Combine(output, "SHA256SUMS.txt"))
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToArray();
            Assert.Equal(9, checksumLines.Length);
            var checksumPackageNames = checksumLines
                .Select(line => line[(line.IndexOf("  ", StringComparison.Ordinal) + 2)..])
                .ToArray();
            Assert.Equal(
                PackageDefinitions
                    .Select(definition => definition.PackageName)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                checksumPackageNames);

            using var manifest = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(output, "release-manifest.json")));
            var rootElement = manifest.RootElement;
            Assert.Equal(1, rootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(Version, rootElement.GetProperty("applicationVersion").GetString());
            Assert.Equal(9, rootElement.GetProperty("packages").GetArrayLength());
            Assert.Equal(6, rootElement.GetProperty("runtimePayloads").GetArrayLength());

            var packageNames = rootElement
                .GetProperty("packages")
                .EnumerateArray()
                .Select(package => package.GetProperty("name").GetString() ?? string.Empty)
                .ToArray();
            Assert.Equal(
                PackageDefinitions
                    .Select(definition => definition.PackageName)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                packageNames);

            Assert.Equal(9, Directory.EnumerateFiles(artifacts, "*.sha256", SearchOption.AllDirectories).Count());
            Assert.Equal(
                9,
                Directory.EnumerateFiles(
                    artifacts,
                    "publish-manifest-*.json",
                    SearchOption.AllDirectories).Count());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AssemblerRejectsPackageThatDoesNotMatchItsSidecar()
    {
        var root = CreateTemporaryDirectory();
        var artifacts = Path.Combine(root, "downloaded-artifacts");
        var output = Path.Combine(root, "release-assets");

        try
        {
            WriteFixture(artifacts);
            var package = Directory
                .EnumerateFiles(
                    artifacts,
                    PackageDefinitions[0].PackageName,
                    SearchOption.AllDirectories)
                .Single();
            File.AppendAllText(package, "tampered", Encoding.UTF8);

            var result = RunAssembler(artifacts, output);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("does not match its checksum sidecar", result.StandardError, StringComparison.Ordinal);
            Assert.False(Directory.Exists(output));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AssemblerRejectsDivergentPayloadsForTheSameRuntime()
    {
        var root = CreateTemporaryDirectory();
        var artifacts = Path.Combine(root, "downloaded-artifacts");
        var output = Path.Combine(root, "release-assets");

        try
        {
            WriteFixture(artifacts);
            var manifestPath = Directory
                .EnumerateFiles(
                    artifacts,
                    "publish-manifest-linux-x64-deb.json",
                    SearchOption.AllDirectories)
                .Single();
            var divergentManifest = CreateManifest("linux-x64", "different-runtime-file");
            File.WriteAllText(
                manifestPath,
                JsonSerializer.Serialize(divergentManifest),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var result = RunAssembler(artifacts, output);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("do not describe the same payload", result.StandardError, StringComparison.Ordinal);
            Assert.False(Directory.Exists(output));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void WriteFixture(string artifacts)
    {
        foreach (var definition in PackageDefinitions)
        {
            var directory = Path.Combine(
                artifacts,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(definition.PackageName)))[..12]);
            Directory.CreateDirectory(directory);

            var packagePath = Path.Combine(directory, definition.PackageName);
            File.WriteAllText(packagePath, $"package:{definition.PackageName}", Encoding.UTF8);
            var packageHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(packagePath)));
            File.WriteAllText(
                $"{packagePath}.sha256",
                $"{packageHash}  {definition.PackageName}\n",
                Encoding.ASCII);

            var manifest = CreateManifest(definition.RuntimeIdentifier, definition.RuntimeIdentifier);
            File.WriteAllText(
                Path.Combine(directory, definition.ManifestName),
                JsonSerializer.Serialize(manifest),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    private static object CreateManifest(string runtimeIdentifier, string payloadIdentity)
    {
        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadIdentity)));
        return new
        {
            schemaVersion = 1,
            runtimeIdentifier,
            applicationVersion = Version,
            generatedAtUtc = "2026-10-08T00:00:00Z",
            files = new[]
            {
                new
                {
                    path = "DownKyi.fixture",
                    bytes = 1,
                    sha256 = payloadHash
                }
            }
        };
    }

    private static ProcessResult RunAssembler(string artifacts, string output)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(Path.Combine(RepositoryRoot, "script", "assemble-release-assets.ps1"));
        startInfo.ArgumentList.Add("-ArtifactsDirectory");
        startInfo.ArgumentList.Add(artifacts);
        startInfo.ArgumentList.Add("-ExpectedVersion");
        startInfo.ArgumentList.Add(Version);
        startInfo.ArgumentList.Add("-OutputDirectory");
        startInfo.ArgumentList.Add(output);

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, standardOutput, standardError);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-release-publication-{Guid.NewGuid():N}");
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

    private sealed record PackageDefinition(
        string PackageName,
        string ManifestName,
        string RuntimeIdentifier);

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
