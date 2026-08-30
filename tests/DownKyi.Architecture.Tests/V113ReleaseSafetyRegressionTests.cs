using System.Buffers.Binary;
using System.Diagnostics;
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
        Assert.Equal(2, CountOccurrences(workflow, "-ExpectedManifestPath"));
        Assert.Contains("verify-dmg-contents.sh DownKyi-", workflow, StringComparison.Ordinal);
        Assert.Contains("ubuntu-24.04-arm", workflow, StringComparison.Ordinal);
        Assert.Contains("appimage-${{ matrix.cpu }}.transport.tar", workflow, StringComparison.Ordinal);
        Assert.Contains("Transported AppImage lost non-owner execute permission", workflow, StringComparison.Ordinal);
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
            var ffmpeg = Path.Combine(runtime, "ffmpeg", "ffmpeg.exe");
            WritePeFile(ffmpeg, 0x8664);
            WritePeFile(Path.Combine(runtime, "ffmpeg", "ffprobe.exe"), 0x8664);
            File.WriteAllText(Path.Combine(runtime, "DownKyi.deps.json"), "{\"libraries\":{\"Avalonia.Themes.Fluent/fixture\":{}}}");
            WriteNonEmptyFile(Path.Combine(runtime, "Avalonia.Themes.Fluent.dll"));
            var desktopDependency = Path.Combine(runtime, "DownKyi.Desktop.dll");
            WriteNonEmptyFile(desktopDependency);
            var expectedManifest = Path.Combine(root, "expected-publish-manifest.json");
            var expected = RunPowerShell(
                Path.Combine(RepositoryRoot, "script", "validate-publish-output.ps1"),
                [
                    "-PublishDirectory", runtime,
                    "-RuntimeIdentifier", "win-x64",
                    "-ExpectedVersion", "1.1.3",
                    "-OutputPath", expectedManifest
                ],
                root);
            Assert.Equal(0, expected.ExitCode);

            var validPackage = Path.Combine(root, "valid.zip");
            ZipFile.CreateFromDirectory(runtime, validPackage);
            var valid = RunPowerShell(
                validator,
                [
                    "-PackagePath", validPackage,
                    "-PackageKind", "zip",
                    "-RuntimeIdentifier", "win-x64",
                    "-ExpectedManifestPath", expectedManifest,
                    "-OutputPath", Path.Combine(root, "valid-manifest.json")
                ],
                root);
            Assert.Equal(0, valid.ExitCode);

            File.Delete(desktopDependency);
            var omittedDependencyPackage = Path.Combine(root, "omitted-dependency.zip");
            ZipFile.CreateFromDirectory(runtime, omittedDependencyPackage);
            var omittedDependency = RunPowerShell(
                validator,
                [
                    "-PackagePath", omittedDependencyPackage,
                    "-PackageKind", "zip",
                    "-RuntimeIdentifier", "win-x64",
                    "-ExpectedManifestPath", expectedManifest,
                    "-OutputPath", Path.Combine(root, "omitted-dependency-manifest.json")
                ],
                root);
            Assert.NotEqual(0, omittedDependency.ExitCode);
            Assert.Contains("does not match the validated publish manifest", NormalizeDiagnostic(omittedDependency), StringComparison.Ordinal);

            WriteNonEmptyFile(desktopDependency);
            File.AppendAllText(desktopDependency, "corrupted");
            var corruptedDependencyPackage = Path.Combine(root, "corrupted-dependency.zip");
            ZipFile.CreateFromDirectory(runtime, corruptedDependencyPackage);
            var corruptedDependency = RunPowerShell(
                validator,
                [
                    "-PackagePath", corruptedDependencyPackage,
                    "-PackageKind", "zip",
                    "-RuntimeIdentifier", "win-x64",
                    "-ExpectedManifestPath", expectedManifest,
                    "-OutputPath", Path.Combine(root, "corrupted-dependency-manifest.json")
                ],
                root);
            Assert.NotEqual(0, corruptedDependency.ExitCode);
            Assert.Contains("does not match the validated publish manifest", NormalizeDiagnostic(corruptedDependency), StringComparison.Ordinal);
            WriteNonEmptyFile(desktopDependency);

            File.AppendAllText(ffmpeg, "substituted");
            var substitutedPackage = Path.Combine(root, "substituted.zip");
            ZipFile.CreateFromDirectory(runtime, substitutedPackage);
            var substituted = RunPowerShell(
                validator,
                [
                    "-PackagePath", substitutedPackage,
                    "-PackageKind", "zip",
                    "-RuntimeIdentifier", "win-x64",
                    "-ExpectedManifestPath", expectedManifest,
                    "-OutputPath", Path.Combine(root, "substituted-manifest.json")
                ],
                root);
            Assert.NotEqual(0, substituted.ExitCode);
            Assert.Contains("does not match the validated publish manifest", NormalizeDiagnostic(substituted), StringComparison.Ordinal);
            WritePeFile(ffmpeg, 0x8664);

            WritePeFile(aria, 0x014c);
            var wrongArchitecturePackage = Path.Combine(root, "wrong-architecture.zip");
            ZipFile.CreateFromDirectory(runtime, wrongArchitecturePackage);
            var wrongArchitecture = RunPowerShell(
                validator,
                [
                    "-PackagePath", wrongArchitecturePackage,
                    "-PackageKind", "zip",
                    "-RuntimeIdentifier", "win-x64",
                    "-ExpectedManifestPath", expectedManifest,
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
                    "-ExpectedManifestPath", expectedManifest,
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
                    "-ExpectedManifestPath", expectedManifest,
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
                    "-ExpectedManifestPath", package + ".expected.json",
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
                    "-ExpectedManifestPath", package + ".expected.json",
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
                    "-ExpectedManifestPath", package + ".expected.json",
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
    public static void LinuxReleasePackageValidationRejectsCrossFormatBinary()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var package = CreateLinuxDebPackage(
                root,
                "amd64",
                includeExecuteBits: true,
                crossFormatExecutable: true);
            var result = RunPowerShell(
                Path.Combine(RepositoryRoot, "script", "validate-v113-release-package.ps1"),
                [
                    "-PackagePath", package,
                    "-PackageKind", "deb",
                    "-RuntimeIdentifier", "linux-x64",
                    "-ExpectedManifestPath", package + ".expected.json",
                    "-OutputPath", Path.Combine(root, "cross-format-manifest.json")
                ],
                root);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("does not match linux-x64", NormalizeDiagnostic(result), StringComparison.Ordinal);
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
    public static void LinuxReleasePackageValidationRejectsMissingAppImageEntrypoint()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var validator = Path.Combine(RepositoryRoot, "script", "validate-v113-release-package.ps1");
            var brokenAppRunFixture = CreateLinuxAppImageFixture(
                Path.Combine(root, "broken-app-run"),
                "#!/bin/sh\nexit 0\n");
            var brokenAppRun = RunPowerShell(
                validator,
                [
                    "-PackagePath", brokenAppRunFixture.Package,
                    "-PackageKind", "AppImage",
                    "-RuntimeIdentifier", "linux-x64",
                    "-ExpectedManifestPath", brokenAppRunFixture.ExpectedManifest,
                    "-OutputPath", Path.Combine(root, "broken-app-run-manifest.json")
                ],
                root);
            Assert.NotEqual(0, brokenAppRun.ExitCode);
            Assert.Contains("AppRun launch smoke exited", NormalizeDiagnostic(brokenAppRun), StringComparison.Ordinal);

            var wrongStubFixture = CreateLinuxAppImageFixture(
                Path.Combine(root, "wrong-stub"),
                "#!/bin/sh\nsleep 30\n");
            var wrongStub = RunPowerShell(
                validator,
                [
                    "-PackagePath", wrongStubFixture.Package,
                    "-PackageKind", "AppImage",
                    "-RuntimeIdentifier", "linux-x64",
                    "-ExpectedManifestPath", wrongStubFixture.ExpectedManifest,
                    "-OutputPath", Path.Combine(root, "wrong-stub-manifest.json")
                ],
                root);
            Assert.NotEqual(0, wrongStub.ExitCode);
            Assert.Contains("AppImage runtime launch smoke exited", NormalizeDiagnostic(wrongStub), StringComparison.Ordinal);

            var mutatedFixture = CreateLinuxAppImageFixture(Path.Combine(root, "missing"), appRunBody: null);
            var mutated = RunPowerShell(
                validator,
                [
                    "-PackagePath", mutatedFixture.Package,
                    "-PackageKind", "AppImage",
                    "-RuntimeIdentifier", "linux-x64",
                    "-ExpectedManifestPath", mutatedFixture.ExpectedManifest,
                    "-OutputPath", Path.Combine(root, "missing-appimage-manifest.json")
                ],
                root);
            Assert.NotEqual(0, mutated.ExitCode);
            Assert.Contains("entrypoint AppRun is missing", NormalizeDiagnostic(mutated), StringComparison.Ordinal);
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
                    "-ExpectedManifestPath", debPackage + ".expected.json",
                    "-OutputPath", Path.Combine(root, "deb-version-manifest.json")
                ],
                root);
            Assert.NotEqual(0, debResult.ExitCode);
            Assert.Contains("package version 1.1.2-1 does not match 1.1.3-1", NormalizeDiagnostic(debResult), StringComparison.Ordinal);

            var rpmPackage = CreateLinuxRpmPackage(root, "x86_64", "1.1.2");
            var rpmResult = RunPowerShell(
                validator,
                [
                    "-PackagePath", rpmPackage,
                    "-PackageKind", "rpm",
                    "-RuntimeIdentifier", "linux-x64",
                    "-ExpectedManifestPath", rpmPackage + ".expected.json",
                    "-OutputPath", Path.Combine(root, "rpm-version-manifest.json")
                ],
                root);
            Assert.NotEqual(0, rpmResult.ExitCode);
            Assert.Contains("package EVR 0:1.1.2-1 does not match 0:1.1.3-1", NormalizeDiagnostic(rpmResult), StringComparison.Ordinal);
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
    public static void LinuxReleasePackageValidationRejectsRpmEvrMismatch()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var validator = Path.Combine(RepositoryRoot, "script", "validate-v113-release-package.ps1");
            var releasePackage = CreateLinuxRpmPackage(root, "x86_64", "1.1.3", release: "2");
            var releaseResult = RunPowerShell(
                validator,
                [
                    "-PackagePath", releasePackage,
                    "-PackageKind", "rpm",
                    "-RuntimeIdentifier", "linux-x64",
                    "-ExpectedManifestPath", releasePackage + ".expected.json",
                    "-OutputPath", Path.Combine(root, "rpm-release-manifest.json")
                ],
                root);
            Assert.NotEqual(0, releaseResult.ExitCode);
            Assert.Contains("package EVR 0:1.1.3-2 does not match 0:1.1.3-1", NormalizeDiagnostic(releaseResult), StringComparison.Ordinal);

            var epochPackage = CreateLinuxRpmPackage(root, "x86_64", "1.1.3", epoch: 1);
            var epochResult = RunPowerShell(
                validator,
                [
                    "-PackagePath", epochPackage,
                    "-PackageKind", "rpm",
                    "-RuntimeIdentifier", "linux-x64",
                    "-ExpectedManifestPath", epochPackage + ".expected.json",
                    "-OutputPath", Path.Combine(root, "rpm-epoch-manifest.json")
                ],
                root);
            Assert.NotEqual(0, epochResult.ExitCode);
            Assert.Contains("package EVR 1:1.1.3-1 does not match 0:1.1.3-1", NormalizeDiagnostic(epochResult), StringComparison.Ordinal);
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
    public static void LinuxReleasePackageValidationRejectsPackageManagerIdentityMismatch()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var validator = Path.Combine(RepositoryRoot, "script", "validate-v113-release-package.ps1");
            var debPackage = CreateLinuxDebPackage(
                root,
                "amd64",
                includeExecuteBits: true,
                packageName: "downkyi-fixture");
            var debResult = RunPowerShell(
                validator,
                [
                    "-PackagePath", debPackage,
                    "-PackageKind", "deb",
                    "-RuntimeIdentifier", "linux-x64",
                    "-ExpectedManifestPath", debPackage + ".expected.json",
                    "-OutputPath", Path.Combine(root, "deb-identity-manifest.json")
                ],
                root);
            Assert.NotEqual(0, debResult.ExitCode);
            Assert.Contains("package identity downkyi-fixture does not match downkyi", NormalizeDiagnostic(debResult), StringComparison.Ordinal);

            var rpmPackage = CreateLinuxRpmPackage(root, "x86_64", "1.1.3", "downkyi-fixture");
            var rpmResult = RunPowerShell(
                validator,
                [
                    "-PackagePath", rpmPackage,
                    "-PackageKind", "rpm",
                    "-RuntimeIdentifier", "linux-x64",
                    "-ExpectedManifestPath", rpmPackage + ".expected.json",
                    "-OutputPath", Path.Combine(root, "rpm-identity-manifest.json")
                ],
                root);
            Assert.NotEqual(0, rpmResult.ExitCode);
            Assert.Contains("package identity downkyi-fixture does not match downkyi", NormalizeDiagnostic(rpmResult), StringComparison.Ordinal);
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
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x86), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x94), 0xf0);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x96), 0x22);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x98), 0x20b);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0xdc), 3);
        File.WriteAllBytes(path, image);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static string CreateLinuxDebPackage(
        string root,
        string architecture,
        bool includeExecuteBits,
        bool ownerOnlyExecute = false,
        string version = "1.1.3",
        string packageName = "downkyi",
        bool crossFormatExecutable = false)
    {
        var packageRoot = Path.Combine(root, $"deb-{architecture}-{includeExecuteBits}-{ownerOnlyExecute}-{version}-{packageName}");
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
        if (crossFormatExecutable)
        {
            WritePeFile(executables[0], 0x8664);
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
            $"Package: {packageName}\nVersion: {version}-1\nArchitecture: {architecture}\nMaintainer: fixture@example.invalid\nDescription: DownKyi release validator fixture\n");

        var package = Path.Combine(root, $"fixture-{architecture}-{includeExecuteBits}-{ownerOnlyExecute}-{version}-{packageName}.deb");
        WriteExpectedManifest(
            runtime,
            architecture == "amd64" ? "linux-x64" : "linux-arm64",
            package + ".expected.json",
            root);
        RunRequired("dpkg-deb", ["--root-owner-group", "--build", packageRoot, package], root);
        return package;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static string CreateLinuxRpmPackage(
        string root,
        string architecture,
        string version,
        string packageName = "downkyi",
        string release = "1",
        int? epoch = null)
    {
        var topDirectory = Path.Combine(root, $"rpm-{architecture}-{version}-{packageName}-{release}-{epoch ?? 0}");
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
        var epochLine = epoch.HasValue ? $"Epoch: {epoch.Value}\n" : string.Empty;
        File.WriteAllText(
            specPath,
            $$"""
            %global _build_id_links none
            Name: {{packageName}}
            {{epochLine}}Version: {{version}}
            Release: {{release}}
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
        var package = Directory.GetFiles(Path.Combine(topDirectory, "RPMS", architecture), "*.rpm").Single();
        WriteExpectedManifest(runtime, "linux-x64", package + ".expected.json", root);
        return package;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static (string Package, string ExpectedManifest) CreateLinuxAppImageFixture(
        string root,
        string? appRunBody)
    {
        Directory.CreateDirectory(root);
        var debPackage = CreateLinuxDebPackage(root, "amd64", includeExecuteBits: true);
        var appRoot = Path.Combine(root, "app-root");
        RunRequired("dpkg-deb", ["--extract", debPackage, appRoot], root);
        var runtime = Path.Combine(appRoot, "usr", "lib", "downkyi");
        var expectedManifest = Path.Combine(root, "appimage-expected.json");
        WriteExpectedManifest(runtime, "linux-x64", expectedManifest, root);

        if (appRunBody is not null)
        {
            var appRun = Path.Combine(appRoot, "AppRun");
            File.WriteAllText(appRun, appRunBody);
            File.SetUnixFileMode(
                appRun,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        var archive = Path.Combine(root, "payload.zip");
        ZipFile.CreateFromDirectory(appRoot, archive);
        var package = Path.Combine(root, appRunBody is null ? "missing-app-run.AppImage" : "fixture.AppImage");
        using (var output = File.Create(package))
        {
            output.Write(File.ReadAllBytes("/bin/true"));
            output.Write(File.ReadAllBytes(archive));
        }
        using (var stream = File.Open(package, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.Position = 8;
            stream.Write([0x41, 0x49, 0x02]);
        }
        File.SetUnixFileMode(
            package,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return (package, expectedManifest);
    }

    private static void WriteExpectedManifest(
        string runtime,
        string runtimeIdentifier,
        string outputPath,
        string workingDirectory)
    {
        var result = RunPowerShell(
            Path.Combine(RepositoryRoot, "script", "validate-publish-output.ps1"),
            [
                "-PublishDirectory", runtime,
                "-RuntimeIdentifier", runtimeIdentifier,
                "-ExpectedVersion", "1.1.3",
                "-OutputPath", outputPath
            ],
            workingDirectory);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to create expected publish manifest: {NormalizeDiagnostic(result)}");
        }
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
