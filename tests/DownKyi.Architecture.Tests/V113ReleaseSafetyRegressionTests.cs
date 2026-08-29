using System.Diagnostics;
using System.Buffers.Binary;
using System.IO.Compression;

namespace DownKyi.Architecture.Tests;

public sealed class V113ReleaseSafetyRegressionTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void GenericReleaseWorkflowInvokesFailClosedReleaseGates()
    {
        var workflow = File.ReadAllText(Path.Combine(RepositoryRoot, ".github", "workflows", "build.yml"));

        Assert.Contains("validate-v113-release-subject.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("resolve-v112-macos-trust.ps1", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("HAS_MACOS_SIGNING: ${{ secrets.", workflow, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(workflow, "validate-v113-release-package.ps1"));
    }

    [Fact]
    public void ReleaseTagProvenanceRejectsLightweightAndNonMainTags()
    {
        var root = CreateTemporaryDirectory();
        var remote = Path.Combine(root, "remote.git");
        var repository = Path.Combine(root, "repository");
        var validator = Path.Combine(RepositoryRoot, "script", "validate-v113-release-subject.ps1");

        try
        {
            RunRequired("git", ["init", "--bare", remote], root);
            RunRequired("git", ["init", "-b", "main", repository], root);
            RunRequired("git", ["config", "user.name", "Release Fixture"], repository);
            RunRequired("git", ["config", "user.email", "release-fixture@example.invalid"], repository);
            File.WriteAllText(Path.Combine(repository, "fixture.txt"), "main");
            File.WriteAllText(Path.Combine(repository, "version.txt"), "1.1.3");
            RunRequired("git", ["add", "fixture.txt", "version.txt"], repository);
            RunRequired("git", ["commit", "-m", "main fixture"], repository);
            RunRequired("git", ["remote", "add", "origin", remote], repository);
            RunRequired("git", ["push", "-u", "origin", "main"], repository);
            var mainCommit = RunRequired("git", ["rev-parse", "HEAD"], repository).StandardOutput.Trim();

            RunRequired("git", ["tag", "-a", "v1.1.3", "-m", "v1.1.3"], repository);
            var valid = RunPowerShell(
                validator,
                ["-SubjectDirectory", repository, "-ReleaseVersion", "v1.1.3", "-SubjectSha", mainCommit],
                repository);
            Assert.Equal(0, valid.ExitCode);

            RunRequired("git", ["tag", "-d", "v1.1.3"], repository);
            RunRequired("git", ["tag", "v1.1.3"], repository);
            var lightweight = RunPowerShell(
                validator,
                ["-SubjectDirectory", repository, "-ReleaseVersion", "v1.1.3", "-SubjectSha", mainCommit],
                repository);
            Assert.NotEqual(0, lightweight.ExitCode);
            Assert.Contains("annotated tag", NormalizeDiagnostic(lightweight), StringComparison.OrdinalIgnoreCase);

            RunRequired("git", ["tag", "-d", "v1.1.3"], repository);
            File.WriteAllText(Path.Combine(repository, "version.txt"), "1.1.4");
            RunRequired("git", ["add", "version.txt"], repository);
            RunRequired("git", ["commit", "-m", "mismatched version fixture"], repository);
            RunRequired("git", ["push", "origin", "main"], repository);
            var mismatchedMainCommit = RunRequired("git", ["rev-parse", "HEAD"], repository).StandardOutput.Trim();
            RunRequired("git", ["tag", "-a", "v1.1.3", "-m", "v1.1.3"], repository);
            var mismatchedVersion = RunPowerShell(
                validator,
                ["-SubjectDirectory", repository, "-ReleaseVersion", "v1.1.3", "-SubjectSha", mismatchedMainCommit],
                repository);
            Assert.NotEqual(0, mismatchedVersion.ExitCode);
            Assert.Contains("version.txt is 1.1.4", NormalizeDiagnostic(mismatchedVersion), StringComparison.Ordinal);

            File.WriteAllText(Path.Combine(repository, "version.txt"), "1.1.3");
            RunRequired("git", ["add", "version.txt"], repository);
            RunRequired("git", ["commit", "-m", "restore release version fixture"], repository);
            RunRequired("git", ["push", "origin", "main"], repository);

            RunRequired("git", ["checkout", "-b", "release-fixture"], repository);
            File.AppendAllText(Path.Combine(repository, "fixture.txt"), "\nrelease-only");
            RunRequired("git", ["add", "fixture.txt"], repository);
            RunRequired("git", ["commit", "-m", "release-only fixture"], repository);
            var releaseOnlyCommit = RunRequired("git", ["rev-parse", "HEAD"], repository).StandardOutput.Trim();
            RunRequired("git", ["tag", "-f", "-a", "v1.1.3", "-m", "v1.1.3"], repository);
            var remoteMain = RunRequired("git", ["rev-parse", "refs/remotes/origin/main"], repository).StandardOutput.Trim();
            Assert.NotEqual(remoteMain, releaseOnlyCommit);
            Assert.Equal("tag", RunRequired("git", ["cat-file", "-t", "v1.1.3"], repository).StandardOutput.Trim());
            Assert.Equal(releaseOnlyCommit, RunRequired("git", ["rev-list", "-n", "1", "v1.1.3"], repository).StandardOutput.Trim());
            Assert.Equal("1.1.3", File.ReadAllText(Path.Combine(repository, "version.txt")));
            var nonMain = RunPowerShell(
                validator,
                ["-SubjectDirectory", repository, "-ReleaseVersion", "v1.1.3", "-SubjectSha", releaseOnlyCommit],
                repository);
            Assert.NotEqual(0, nonMain.ExitCode);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void MacOsReleaseTrustRejectsPartialCredentials()
    {
        var root = CreateTemporaryDirectory();
        var output = Path.Combine(root, "trust.json");
        var resolver = Path.Combine(RepositoryRoot, "script", "resolve-v112-macos-trust.ps1");

        try
        {
            var adHoc = RunPowerShell(resolver, ["-OutputPath", output], root);
            Assert.Equal(0, adHoc.ExitCode);
            Assert.Contains("ad-hoc", File.ReadAllText(output), StringComparison.Ordinal);

            var partial = RunPowerShell(
                resolver,
                ["-OutputPath", output],
                root,
                new Dictionary<string, string> { ["APPLE_ID"] = "fixture@example.invalid" });
            Assert.NotEqual(0, partial.ExitCode);
            Assert.Contains("Partial Apple credentials", NormalizeDiagnostic(partial), StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void ReleasePackageValidationRejectsMutatedZipContents()
    {
        var root = CreateTemporaryDirectory();
        var runtime = Path.Combine(root, "runtime");
        Directory.CreateDirectory(Path.Combine(runtime, "aria2"));
        Directory.CreateDirectory(Path.Combine(runtime, "ffmpeg"));
        var validator = Path.Combine(RepositoryRoot, "script", "validate-v113-release-package.ps1");

        try
        {
            File.Copy(typeof(V113ReleaseSafetyRegressionTests).Assembly.Location, Path.Combine(runtime, "DownKyi.dll"));
            WritePeFile(Path.Combine(runtime, "DownKyi.exe"), 0x8664);
            var aria = Path.Combine(runtime, "aria2", "aria2c.exe");
            WritePeFile(aria, 0x8664);
            var ariaHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(aria)));
            File.WriteAllText(Path.Combine(runtime, "aria2", "aria2c.exe.sha256"), ariaHash);
            WritePeFile(Path.Combine(runtime, "ffmpeg", "ffmpeg.exe"), 0x8664);
            WritePeFile(Path.Combine(runtime, "ffmpeg", "ffprobe.exe"), 0x8664);
            File.WriteAllText(Path.Combine(runtime, "DownKyi.deps.json"), "{\"libraries\":{\"Avalonia.Themes.Fluent/fixture\":{}}}");
            WriteNonEmptyFile(Path.Combine(runtime, "Avalonia.Themes.Fluent.dll"));

            var validPackage = Path.Combine(root, "valid.zip");
            ZipFile.CreateFromDirectory(runtime, validPackage);
            var valid = RunPowerShell(
                validator,
                [
                    "-PackagePath", validPackage,
                    "-PackageKind", "zip",
                    "-RuntimeIdentifier", "win-x64",
                    "-OutputPath", Path.Combine(root, "valid-manifest.json")
                ],
                root);
            Assert.Equal(0, valid.ExitCode);

            WritePeFile(aria, 0x014c);
            var wrongArchitecturePackage = Path.Combine(root, "wrong-architecture.zip");
            ZipFile.CreateFromDirectory(runtime, wrongArchitecturePackage);
            var wrongArchitecture = RunPowerShell(
                validator,
                [
                    "-PackagePath", wrongArchitecturePackage,
                    "-PackageKind", "zip",
                    "-RuntimeIdentifier", "win-x64",
                    "-OutputPath", Path.Combine(root, "wrong-architecture-manifest.json")
                ],
                root);
            Assert.NotEqual(0, wrongArchitecture.ExitCode);
            Assert.Contains("does not match win-x64", NormalizeDiagnostic(wrongArchitecture), StringComparison.Ordinal);
            WritePeFile(aria, 0x8664);
            ariaHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(aria)));
            File.WriteAllText(Path.Combine(runtime, "aria2", "aria2c.exe.sha256"), ariaHash);

            File.Delete(Path.Combine(runtime, "Avalonia.Themes.Fluent.dll"));
            var mutatedPackage = Path.Combine(root, "mutated.zip");
            ZipFile.CreateFromDirectory(runtime, mutatedPackage);
            var mutated = RunPowerShell(
                validator,
                [
                    "-PackagePath", mutatedPackage,
                    "-PackageKind", "zip",
                    "-RuntimeIdentifier", "win-x64",
                    "-OutputPath", Path.Combine(root, "mutated-manifest.json")
                ],
                root);
            Assert.NotEqual(0, mutated.ExitCode);
            Assert.Contains("Avalonia Fluent theme assembly", NormalizeDiagnostic(mutated), StringComparison.Ordinal);

            WriteNonEmptyFile(Path.Combine(runtime, "Avalonia.Themes.Fluent.dll"));
            File.Copy(typeof(string).Assembly.Location, Path.Combine(runtime, "DownKyi.dll"), overwrite: true);
            var wrongVersionPackage = Path.Combine(root, "wrong-version.zip");
            ZipFile.CreateFromDirectory(runtime, wrongVersionPackage);
            var wrongVersion = RunPowerShell(
                validator,
                [
                    "-PackagePath", wrongVersionPackage,
                    "-PackageKind", "zip",
                    "-RuntimeIdentifier", "win-x64",
                    "-OutputPath", Path.Combine(root, "wrong-version-manifest.json")
                ],
                root);
            Assert.NotEqual(0, wrongVersion.ExitCode);
            Assert.Contains("does not match expected version", NormalizeDiagnostic(wrongVersion), StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "xUnit",
        "xUnit1013:Public method should be marked as test")]
    public static void LinuxReleasePackageValidationRejectsMissingExecuteBits()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var package = CreateLinuxDebPackage(root, "amd64", includeExecuteBits: false);
            var result = RunPowerShell(
                Path.Combine(RepositoryRoot, "script", "validate-v113-release-package.ps1"),
                [
                    "-PackagePath", package,
                    "-PackageKind", "deb",
                    "-RuntimeIdentifier", "linux-x64",
                    "-OutputPath", Path.Combine(root, "permission-manifest.json")
                ],
                root);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("not executable by a non-owner", NormalizeDiagnostic(result), StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "xUnit",
        "xUnit1013:Public method should be marked as test")]
    public static void LinuxReleasePackageValidationRejectsArchitectureMismatch()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var package = CreateLinuxDebPackage(root, "amd64", includeExecuteBits: true);
            var result = RunPowerShell(
                Path.Combine(RepositoryRoot, "script", "validate-v113-release-package.ps1"),
                [
                    "-PackagePath", package,
                    "-PackageKind", "deb",
                    "-RuntimeIdentifier", "linux-arm64",
                    "-OutputPath", Path.Combine(root, "architecture-manifest.json")
                ],
                root);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("architecture amd64 does not match linux-arm64", NormalizeDiagnostic(result), StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "xUnit",
        "xUnit1013:Public method should be marked as test")]
    public static void LinuxReleasePackageValidationRejectsOwnerOnlyExecuteBits()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var package = CreateLinuxDebPackage(
                root,
                "amd64",
                includeExecuteBits: true,
                ownerOnlyExecute: true);
            var result = RunPowerShell(
                Path.Combine(RepositoryRoot, "script", "validate-v113-release-package.ps1"),
                [
                    "-PackagePath", package,
                    "-PackageKind", "deb",
                    "-RuntimeIdentifier", "linux-x64",
                    "-OutputPath", Path.Combine(root, "owner-only-manifest.json")
                ],
                root);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("not executable by a non-owner", NormalizeDiagnostic(result), StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "xUnit",
        "xUnit1013:Public method should be marked as test")]
    public static void LinuxReleasePackageValidationRejectsPackageManagerVersionMismatch()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var validator = Path.Combine(RepositoryRoot, "script", "validate-v113-release-package.ps1");
            var debPackage = CreateLinuxDebPackage(
                root,
                "amd64",
                includeExecuteBits: true,
                version: "1.1.2");
            var debResult = RunPowerShell(
                validator,
                [
                    "-PackagePath", debPackage,
                    "-PackageKind", "deb",
                    "-RuntimeIdentifier", "linux-x64",
                    "-OutputPath", Path.Combine(root, "deb-version-manifest.json")
                ],
                root);
            Assert.NotEqual(0, debResult.ExitCode);
            Assert.Contains("package version 1.1.2 does not match 1.1.3", NormalizeDiagnostic(debResult), StringComparison.Ordinal);

            var rpmPackage = CreateLinuxRpmPackage(root, "x86_64", "1.1.2");
            var rpmResult = RunPowerShell(
                validator,
                [
                    "-PackagePath", rpmPackage,
                    "-PackageKind", "rpm",
                    "-RuntimeIdentifier", "linux-x64",
                    "-OutputPath", Path.Combine(root, "rpm-version-manifest.json")
                ],
                root);
            Assert.NotEqual(0, rpmResult.ExitCode);
            Assert.Contains("package version 1.1.2 does not match 1.1.3", NormalizeDiagnostic(rpmResult), StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    private static void WriteNonEmptyFile(string path) => File.WriteAllText(path, "fixture");

    private static void WritePeFile(string path, ushort machine)
    {
        var image = new byte[512];
        image[0] = 0x4d;
        image[1] = 0x5a;
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3c), 0x80);
        image[0x80] = 0x50;
        image[0x81] = 0x45;
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x84), machine);
        File.WriteAllBytes(path, image);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static string CreateLinuxDebPackage(
        string root,
        string architecture,
        bool includeExecuteBits,
        bool ownerOnlyExecute = false,
        string version = "1.1.3")
    {
        var packageRoot = Path.Combine(root, $"deb-{architecture}-{includeExecuteBits}-{ownerOnlyExecute}-{version}");
        var controlDirectory = Path.Combine(packageRoot, "DEBIAN");
        var runtime = Path.Combine(packageRoot, "usr", "lib", "downkyi");
        Directory.CreateDirectory(controlDirectory);
        Directory.CreateDirectory(Path.Combine(runtime, "aria2"));
        Directory.CreateDirectory(Path.Combine(runtime, "ffmpeg"));

        File.Copy(typeof(V113ReleaseSafetyRegressionTests).Assembly.Location, Path.Combine(runtime, "DownKyi.dll"));
        var executables = new[]
        {
            Path.Combine(runtime, "DownKyi"),
            Path.Combine(runtime, "aria2", "aria2c"),
            Path.Combine(runtime, "ffmpeg", "ffmpeg"),
            Path.Combine(runtime, "ffmpeg", "ffprobe")
        };
        foreach (var executable in executables)
        {
            File.Copy("/bin/true", executable);
        }

        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        if (includeExecuteBits)
        {
            mode |= UnixFileMode.UserExecute;
            if (!ownerOnlyExecute)
            {
                mode |= UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            }
        }
        foreach (var executable in executables)
        {
            File.SetUnixFileMode(executable, mode);
        }

        var aria = Path.Combine(runtime, "aria2", "aria2c");
        var ariaHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(aria)));
        File.WriteAllText(Path.Combine(runtime, "aria2", "aria2c.sha256"), ariaHash);
        File.WriteAllText(Path.Combine(runtime, "DownKyi.deps.json"), "{\"libraries\":{\"Avalonia.Themes.Fluent/fixture\":{}}}");
        WriteNonEmptyFile(Path.Combine(runtime, "Avalonia.Themes.Fluent.dll"));
        File.WriteAllText(
            Path.Combine(controlDirectory, "control"),
            $"Package: downkyi-fixture\nVersion: {version}\nArchitecture: {architecture}\nMaintainer: fixture@example.invalid\nDescription: DownKyi release validator fixture\n");

        var package = Path.Combine(root, $"fixture-{architecture}-{includeExecuteBits}-{ownerOnlyExecute}-{version}.deb");
        RunRequired("dpkg-deb", ["--root-owner-group", "--build", packageRoot, package], root);
        return package;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static string CreateLinuxRpmPackage(string root, string architecture, string version)
    {
        var topDirectory = Path.Combine(root, $"rpm-{architecture}-{version}");
        var runtime = Path.Combine(topDirectory, "payload", "usr", "lib", "downkyi");
        foreach (var directory in new[] { "BUILD", "BUILDROOT", "RPMS", "SOURCES", "SPECS", "SRPMS" })
        {
            Directory.CreateDirectory(Path.Combine(topDirectory, directory));
        }
        Directory.CreateDirectory(Path.Combine(runtime, "aria2"));
        Directory.CreateDirectory(Path.Combine(runtime, "ffmpeg"));

        File.Copy(typeof(V113ReleaseSafetyRegressionTests).Assembly.Location, Path.Combine(runtime, "DownKyi.dll"));
        var executables = new[]
        {
            Path.Combine(runtime, "DownKyi"),
            Path.Combine(runtime, "aria2", "aria2c"),
            Path.Combine(runtime, "ffmpeg", "ffmpeg"),
            Path.Combine(runtime, "ffmpeg", "ffprobe")
        };
        foreach (var executable in executables)
        {
            File.Copy("/bin/true", executable);
            File.SetUnixFileMode(
                executable,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        var aria = Path.Combine(runtime, "aria2", "aria2c");
        var ariaHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(aria)));
        File.WriteAllText(Path.Combine(runtime, "aria2", "aria2c.sha256"), ariaHash);
        File.WriteAllText(Path.Combine(runtime, "DownKyi.deps.json"), "{\"libraries\":{\"Avalonia.Themes.Fluent/fixture\":{}}}");
        WriteNonEmptyFile(Path.Combine(runtime, "Avalonia.Themes.Fluent.dll"));

        var specPath = Path.Combine(topDirectory, "SPECS", "downkyi-fixture.spec");
        File.WriteAllText(
            specPath,
            $$"""
            %global _build_id_links none
            Name: downkyi-fixture
            Version: {{version}}
            Release: 1
            Summary: DownKyi release validator fixture
            License: MIT
            BuildArch: {{architecture}}
            AutoReqProv: no

            %description
            DownKyi release validator fixture.

            %install
            mkdir -p %{buildroot}/usr/lib/downkyi
            cp -a "{{runtime}}/." %{buildroot}/usr/lib/downkyi/

            %files
            /usr/lib/downkyi
            """);
        RunRequired("rpmbuild", ["-bb", "--define", $"_topdir {topDirectory}", specPath], root);
        return Directory.GetFiles(Path.Combine(topDirectory, "RPMS", architecture), "*.rpm").Single();
    }

    private static int CountOccurrences(string source, string value) =>
        source.Split(value, StringSplitOptions.None).Length - 1;

    private static string NormalizeDiagnostic(ProcessResult result) =>
        System.Text.RegularExpressions.Regex.Replace(
            $"{result.StandardOutput}\n{result.StandardError}",
            @"\s+",
            " ");

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"downkyi-release-gate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(path, recursive: true);
    }

    private static ProcessResult RunPowerShell(
        string script,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var allArguments = new List<string> { "-NoLogo", "-NoProfile", "-File", script };
        allArguments.AddRange(arguments);
        return RunProcess("pwsh", allArguments, workingDirectory, environment);
    }

    private static ProcessResult RunRequired(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        var result = RunProcess(executable, arguments, workingDirectory);
        Assert.True(result.ExitCode == 0, $"{executable} failed: {result.StandardError}");
        return result;
    }

    private static ProcessResult RunProcess(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var name in new[] { "MACOS_CERTIFICATE", "MACOS_CERTIFICATE_PWD", "APPLE_ID", "TEAM_ID", "APP_SPECIFIC_PASSWORD" })
        {
            startInfo.Environment.Remove(name);
        }
        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(30_000), $"Process timed out: {executable}");
        return new ProcessResult(process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "version.txt")) &&
                Directory.Exists(Path.Combine(current.FullName, ".github")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
