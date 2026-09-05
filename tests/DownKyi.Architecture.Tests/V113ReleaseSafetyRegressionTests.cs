using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using DownKyi.TestInfrastructure;

namespace DownKyi.Architecture.Tests;

public sealed class V113ReleaseSafetyRegressionTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void GenericReleaseWorkflowInvokesFailClosedReleaseGates()
    {
        var workflow = File.ReadAllText(Path.Combine(RepositoryRoot, ".github", "workflows", "build.yml"));
        var packageValidator = File.ReadAllText(
            Path.Combine(RepositoryRoot, "script", "validate-v113-release-package.ps1"));

        Assert.Contains("validate-v113-release-subject.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("resolve-v112-macos-trust.ps1", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("HAS_MACOS_SIGNING: ${{ secrets.", workflow, StringComparison.Ordinal);
        Assert.Equal(3, CountOccurrences(workflow, "validate-v113-release-package.ps1"));
        Assert.Equal(3, CountOccurrences(workflow, "-ExpectedManifestPath"));
        Assert.Contains("verify-dmg-contents.sh DownKyi-", workflow, StringComparison.Ordinal);
        Assert.Contains("ubuntu-24.04-arm", workflow, StringComparison.Ordinal);
        Assert.Contains("validate-linux-arm64:", workflow, StringComparison.Ordinal);
        Assert.Contains("linux-arm64-${{ matrix.kind }}.candidate.internal.transport.tar", workflow, StringComparison.Ordinal);
        Assert.Contains("appimage-${{ matrix.cpu }}.transport.tar", workflow, StringComparison.Ordinal);
        Assert.Contains("Transported AppImage lost non-owner execute permission", workflow, StringComparison.Ordinal);
        Assert.Contains("'--appimage-extract'", packageValidator, StringComparison.Ordinal);
        Assert.Contains("LinkType -ceq 'SymbolicLink'", packageValidator, StringComparison.Ordinal);
        Assert.Contains("usr/bin/DownKyi", packageValidator, StringComparison.Ordinal);
        Assert.Contains("Test-ElfFile", packageValidator, StringComparison.Ordinal);
        Assert.Contains(
            "Assert-LinuxBinaryArchitecture -Path $executable",
            packageValidator,
            StringComparison.Ordinal);
        Assert.DoesNotContain("& 7z", packageValidator, StringComparison.Ordinal);
    }

    [Fact]
    public void Arm64CandidatePromotionRejectsBrokenWorkflowTransitions()
    {
        var workflow = File.ReadAllText(Path.Combine(RepositoryRoot, ".github", "workflows", "build.yml"));

        AssertArm64PromotionContract(workflow);
        Assert.ThrowsAny<Exception>(() => AssertArm64PromotionContract(
            workflow.Replace(
                "needs: [changelog, build-windows, build-linux, validate-linux-arm64, build-macos]",
                "needs: [changelog, build-windows, build-linux, build-macos]",
                StringComparison.Ordinal)));
        Assert.ThrowsAny<Exception>(() => AssertArm64PromotionContract(
            workflow.Replace(
                "name: linux-arm64-${{ matrix.kind }}-candidate",
                "name: appimage-arm64-transport",
                StringComparison.Ordinal)));
        Assert.ThrowsAny<Exception>(() => AssertArm64PromotionContract(
            workflow.Replace(
                "Get-ChildItem artifacts -File -Filter '*.internal.transport.tar'",
                "Get-ChildItem artifacts -File -Filter '*.candidate.transport.tar'",
                StringComparison.Ordinal)));
    }

    [Fact]
    public async Task PupNetStandalonePackagingStagesOnlyTheCanonicalValidatedPayload()
    {
        var configuration = await File.ReadAllTextAsync(
            Path.Combine(RepositoryRoot, "script", "pupnet", "DownKyi.pupnet.conf"),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        var workflow = await File.ReadAllTextAsync(
            Path.Combine(RepositoryRoot, ".github", "workflows", "build.yml"),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        var linuxPublish = GetWorkflowJob(workflow, "build-linux-publish");
        var linuxPackages = GetWorkflowJob(workflow, "build-linux");

        Assert.Contains("DotnetProjectPath = NONE", configuration, StringComparison.Ordinal);
        Assert.Contains("DotnetPostPublish = stage-canonical-publish.sh", configuration, StringComparison.Ordinal);
        Assert.Contains("DotnetPostPublishOnWindows = stage-canonical-publish.cmd", configuration, StringComparison.Ordinal);
        Assert.DoesNotContain("DotnetPublishArgs", configuration, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(workflow, "- name: Build canonical publish"));
        Assert.Equal(2, CountOccurrences(workflow, "- name: Finalize and validate canonical publish payload"));
        Assert.Equal(2, CountOccurrences(workflow, "CANONICAL_PUBLISH_DIRECTORY:"));
        Assert.Equal(5, CountOccurrences(workflow, "os: ubuntu-22.04"));
        Assert.Equal(3, CountOccurrences(workflow, "linux-${{ matrix.cpu }}.canonical-publish.internal.transport.tar"));
        Assert.Equal(1, CountOccurrences(linuxPublish, "dotnet publish ./DownKyi/DownKyi.csproj"));
        Assert.Contains("cpu: [ x64, arm64 ]", linuxPublish, StringComparison.Ordinal);
        Assert.DoesNotContain("kind: [ AppImage, deb, rpm ]", linuxPublish, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet publish ./DownKyi/DownKyi.csproj", linuxPackages, StringComparison.Ordinal);
        Assert.Contains("needs: [changelog, build-linux-publish]", linuxPackages, StringComparison.Ordinal);
        Assert.Contains("tools/linux_x64/protoc", workflow, StringComparison.Ordinal);
        Assert.Contains("file artifacts/publish/linux-arm64/DownKyi | grep -qi 'ELF.*aarch64'", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("p7zip-full", workflow, StringComparison.Ordinal);

        var root = CreateTemporaryDirectory();
        var source = Path.Combine(root, "canonical");
        var destination = Path.Combine(root, "pupnet-staging");
        await ExternalProcessTestHarness.RunWithCleanupAsync(
            async () =>
            {
                Directory.CreateDirectory(source);
                Directory.CreateDirectory(destination);
                Directory.CreateDirectory(Path.Combine(source, "nested"));
                await File.WriteAllBytesAsync(
                    Path.Combine(source, "payload.bin"),
                    [0, 1, 2, 3, 255],
                    TestContext.Current.CancellationToken).ConfigureAwait(true);
                await File.WriteAllTextAsync(
                    Path.Combine(source, "nested", "LICENSE"),
                    "canonical-license",
                    TestContext.Current.CancellationToken).ConfigureAwait(true);

                IReadOnlyDictionary<string, string> environment = new Dictionary<string, string>
                {
                    ["CANONICAL_PUBLISH_DIRECTORY"] = source,
                    ["BUILD_APP_BIN"] = destination
                };
                ExternalProcessResult result;
                if (OperatingSystem.IsWindows())
                {
                    result = await RunProcess(
                        "cmd.exe",
                        ["/c", Path.Combine(RepositoryRoot, "script", "pupnet", "stage-canonical-publish.cmd")],
                        root,
                        environment).ConfigureAwait(false);
                }
                else
                {
                    result = await RunProcess(
                        Path.Combine(RepositoryRoot, "script", "pupnet", "stage-canonical-publish.sh"),
                        [],
                        root,
                        environment).ConfigureAwait(false);
                }

                Assert.Equal(0, result.ExitCode);
                Assert.Equal(
                    await File.ReadAllBytesAsync(
                        Path.Combine(source, "payload.bin"),
                        TestContext.Current.CancellationToken).ConfigureAwait(true),
                    await File.ReadAllBytesAsync(
                        Path.Combine(destination, "payload.bin"),
                        TestContext.Current.CancellationToken).ConfigureAwait(true));
                Assert.Equal(
                    "canonical-license",
                    await File.ReadAllTextAsync(
                        Path.Combine(destination, "nested", "LICENSE"),
                        TestContext.Current.CancellationToken).ConfigureAwait(true));

                ExternalProcessResult secondResult;
                if (OperatingSystem.IsWindows())
                {
                    secondResult = await RunProcess(
                        "cmd.exe",
                        ["/c", Path.Combine(RepositoryRoot, "script", "pupnet", "stage-canonical-publish.cmd")],
                        root,
                        environment).ConfigureAwait(false);
                }
                else
                {
                    secondResult = await RunProcess(
                        Path.Combine(RepositoryRoot, "script", "pupnet", "stage-canonical-publish.sh"),
                        [],
                        root,
                        environment).ConfigureAwait(false);
                }
                Assert.NotEqual(0, secondResult.ExitCode);
                Assert.Contains("must be empty", NormalizeDiagnostic(secondResult), StringComparison.Ordinal);
            },
            () => DeleteTemporaryDirectoryAsync(root)).ConfigureAwait(true);
    }

    [Fact]
    public async Task ReleaseTagProvenanceRejectsLightweightAndNonMainTags()
    {
        var root = CreateTemporaryDirectory();
        var remote = Path.Combine(root, "remote.git");
        var repository = Path.Combine(root, "repository");
        var validator = Path.Combine(RepositoryRoot, "script", "validate-v113-release-subject.ps1");

        await ExternalProcessTestHarness.RunWithCleanupAsync(
            async () =>
            {
                await RunRequired("git", ["init", "--bare", remote], root).ConfigureAwait(false);
                await RunRequired("git", ["init", "-b", "main", repository], root).ConfigureAwait(false);
                await RunRequired("git", ["config", "user.name", "Release Fixture"], repository).ConfigureAwait(false);
                await RunRequired("git", ["config", "user.email", "release-fixture@example.invalid"], repository).ConfigureAwait(false);
                await File.WriteAllTextAsync(
                    Path.Combine(repository, "fixture.txt"),
                    "main",
                    TestContext.Current.CancellationToken).ConfigureAwait(true);
                await File.WriteAllTextAsync(
                    Path.Combine(repository, "version.txt"),
                    "1.1.3",
                    TestContext.Current.CancellationToken).ConfigureAwait(true);
                await RunRequired("git", ["add", "fixture.txt", "version.txt"], repository).ConfigureAwait(false);
                await RunRequired("git", ["commit", "-m", "main fixture"], repository).ConfigureAwait(false);
                await RunRequired("git", ["remote", "add", "origin", remote], repository).ConfigureAwait(false);
                await RunRequired("git", ["push", "-u", "origin", "main"], repository).ConfigureAwait(false);
                var mainCommit = (await RunRequired("git", ["rev-parse", "HEAD"], repository).ConfigureAwait(false)).StandardOutput.Trim();

                await RunRequired("git", ["tag", "-a", "v1.1.3", "-m", "v1.1.3"], repository).ConfigureAwait(false);
                var valid = await RunPowerShell(
                    validator,
                    ["-SubjectDirectory", repository, "-ReleaseVersion", "v1.1.3", "-SubjectSha", mainCommit],
                    repository).ConfigureAwait(false);
                Assert.Equal(0, valid.ExitCode);

                await RunRequired("git", ["tag", "-d", "v1.1.3"], repository).ConfigureAwait(false);
                await RunRequired("git", ["tag", "v1.1.3"], repository).ConfigureAwait(false);
                var lightweight = await RunPowerShell(
                    validator,
                    ["-SubjectDirectory", repository, "-ReleaseVersion", "v1.1.3", "-SubjectSha", mainCommit],
                    repository).ConfigureAwait(false);
                Assert.NotEqual(0, lightweight.ExitCode);
                Assert.Contains("annotated tag", NormalizeDiagnostic(lightweight), StringComparison.OrdinalIgnoreCase);

                await RunRequired("git", ["tag", "-d", "v1.1.3"], repository).ConfigureAwait(false);
                await File.WriteAllTextAsync(
                    Path.Combine(repository, "version.txt"),
                    "1.1.4",
                    TestContext.Current.CancellationToken).ConfigureAwait(true);
                await RunRequired("git", ["add", "version.txt"], repository).ConfigureAwait(false);
                await RunRequired("git", ["commit", "-m", "mismatched version fixture"], repository).ConfigureAwait(false);
                await RunRequired("git", ["push", "origin", "main"], repository).ConfigureAwait(false);
                var mismatchedMainCommit = (await RunRequired("git", ["rev-parse", "HEAD"], repository).ConfigureAwait(false)).StandardOutput.Trim();
                await RunRequired("git", ["tag", "-a", "v1.1.3", "-m", "v1.1.3"], repository).ConfigureAwait(false);
                var mismatchedVersion = await RunPowerShell(
                    validator,
                    ["-SubjectDirectory", repository, "-ReleaseVersion", "v1.1.3", "-SubjectSha", mismatchedMainCommit],
                    repository).ConfigureAwait(false);
                Assert.NotEqual(0, mismatchedVersion.ExitCode);
                Assert.Contains("version.txt is 1.1.4", NormalizeDiagnostic(mismatchedVersion), StringComparison.Ordinal);

                await File.WriteAllTextAsync(
                    Path.Combine(repository, "version.txt"),
                    "1.1.3",
                    TestContext.Current.CancellationToken).ConfigureAwait(true);
                await RunRequired("git", ["add", "version.txt"], repository).ConfigureAwait(false);
                await RunRequired("git", ["commit", "-m", "restore release version fixture"], repository).ConfigureAwait(false);
                await RunRequired("git", ["push", "origin", "main"], repository).ConfigureAwait(false);

                await RunRequired("git", ["checkout", "-b", "release-fixture"], repository).ConfigureAwait(false);
                await File.AppendAllTextAsync(
                    Path.Combine(repository, "fixture.txt"),
                    "\nrelease-only",
                    TestContext.Current.CancellationToken).ConfigureAwait(true);
                await RunRequired("git", ["add", "fixture.txt"], repository).ConfigureAwait(false);
                await RunRequired("git", ["commit", "-m", "release-only fixture"], repository).ConfigureAwait(false);
                var releaseOnlyCommit = (await RunRequired("git", ["rev-parse", "HEAD"], repository).ConfigureAwait(false)).StandardOutput.Trim();
                await RunRequired("git", ["tag", "-f", "-a", "v1.1.3", "-m", "v1.1.3"], repository).ConfigureAwait(false);
                var remoteMain = (await RunRequired("git", ["rev-parse", "refs/remotes/origin/main"], repository).ConfigureAwait(false)).StandardOutput.Trim();
                Assert.NotEqual(remoteMain, releaseOnlyCommit);
                Assert.Equal("tag", (await RunRequired("git", ["cat-file", "-t", "v1.1.3"], repository).ConfigureAwait(false)).StandardOutput.Trim());
                Assert.Equal(releaseOnlyCommit, (await RunRequired("git", ["rev-list", "-n", "1", "v1.1.3"], repository).ConfigureAwait(false)).StandardOutput.Trim());
                Assert.Equal(
                    "1.1.3",
                    await File.ReadAllTextAsync(
                        Path.Combine(repository, "version.txt"),
                        TestContext.Current.CancellationToken).ConfigureAwait(true));
                var nonMain = await RunPowerShell(
                    validator,
                    ["-SubjectDirectory", repository, "-ReleaseVersion", "v1.1.3", "-SubjectSha", releaseOnlyCommit],
                    repository).ConfigureAwait(false);
                Assert.NotEqual(0, nonMain.ExitCode);
            },
            () => DeleteTemporaryDirectoryAsync(root)).ConfigureAwait(true);
    }

    [Fact]
    public async Task MacOsReleaseTrustRejectsPartialCredentials()
    {
        var root = CreateTemporaryDirectory();
        var output = Path.Combine(root, "trust.json");
        var resolver = Path.Combine(RepositoryRoot, "script", "resolve-v112-macos-trust.ps1");

        await ExternalProcessTestHarness.RunWithCleanupAsync(
            async () =>
            {
                var adHoc = await RunPowerShell(resolver, ["-OutputPath", output], root).ConfigureAwait(false);
                Assert.Equal(0, adHoc.ExitCode);
                Assert.Contains(
                    "ad-hoc",
                    await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken).ConfigureAwait(true),
                    StringComparison.Ordinal);

                var partial = await RunPowerShell(
                    resolver,
                    ["-OutputPath", output],
                    root,
                    new Dictionary<string, string> { ["APPLE_ID"] = "fixture@example.invalid" }).ConfigureAwait(false);
                Assert.NotEqual(0, partial.ExitCode);
                Assert.Contains("Partial Apple credentials", NormalizeDiagnostic(partial), StringComparison.Ordinal);
            },
            () => DeleteTemporaryDirectoryAsync(root)).ConfigureAwait(true);
    }

    [Fact]
    public async Task ReleasePackageValidationRejectsMutatedZipContents()
    {
        var root = CreateTemporaryDirectory();
        var runtime = Path.Combine(root, "runtime");
        var validator = Path.Combine(RepositoryRoot, "script", "validate-v113-release-package.ps1");

        await ExternalProcessTestHarness.RunWithCleanupAsync(
            async () =>
            {
                Directory.CreateDirectory(Path.Combine(runtime, "aria2"));
                Directory.CreateDirectory(Path.Combine(runtime, "ffmpeg"));
                File.Copy(typeof(V113ReleaseSafetyRegressionTests).Assembly.Location, Path.Combine(runtime, "DownKyi.dll"));
                WritePeFile(Path.Combine(runtime, "DownKyi.exe"), 0x8664);
                var aria = Path.Combine(runtime, "aria2", "aria2c.exe");
                WritePeFile(aria, 0x8664);
                var ariaHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    await File.ReadAllBytesAsync(aria, TestContext.Current.CancellationToken).ConfigureAwait(true)));
                await File.WriteAllTextAsync(
                    Path.Combine(runtime, "aria2", "aria2c.exe.sha256"),
                    ariaHash,
                    TestContext.Current.CancellationToken).ConfigureAwait(true);
                var ffmpeg = Path.Combine(runtime, "ffmpeg", "ffmpeg.exe");
                WritePeFile(ffmpeg, 0x8664);
                WritePeFile(Path.Combine(runtime, "ffmpeg", "ffprobe.exe"), 0x8664);
                await File.WriteAllTextAsync(
                    Path.Combine(runtime, "DownKyi.deps.json"),
                    "{\"libraries\":{\"Avalonia.Themes.Fluent/fixture\":{}}}",
                    TestContext.Current.CancellationToken).ConfigureAwait(true);
                WriteNonEmptyFile(Path.Combine(runtime, "Avalonia.Themes.Fluent.dll"));
                var desktopDependency = Path.Combine(runtime, "DownKyi.Desktop.dll");
                WriteNonEmptyFile(desktopDependency);
                var expectedManifest = Path.Combine(root, "expected-publish-manifest.json");
                var expected = await RunPowerShell(
                    Path.Combine(RepositoryRoot, "script", "validate-publish-output.ps1"),
                    [
                        "-PublishDirectory", runtime,
                    "-RuntimeIdentifier", "win-x64",
                    "-ExpectedVersion", "1.1.3",
                    "-OutputPath", expectedManifest
                    ],
                    root).ConfigureAwait(false);
                Assert.Equal(0, expected.ExitCode);

                var validPackage = Path.Combine(root, "valid.zip");
                await ZipFile.CreateFromDirectoryAsync(
                    runtime,
                    validPackage,
                    TestContext.Current.CancellationToken).ConfigureAwait(true);
                var valid = await RunPowerShell(
                    validator,
                    [
                        "-PackagePath", validPackage,
                    "-PackageKind", "zip",
                    "-RuntimeIdentifier", "win-x64",
                    "-ExpectedManifestPath", expectedManifest,
                    "-OutputPath", Path.Combine(root, "valid-manifest.json")
                    ],
                    root).ConfigureAwait(false);
                Assert.Equal(0, valid.ExitCode);

                File.Delete(desktopDependency);
                var omittedDependencyPackage = Path.Combine(root, "omitted-dependency.zip");
                await ZipFile.CreateFromDirectoryAsync(
                    runtime,
                    omittedDependencyPackage,
                    TestContext.Current.CancellationToken).ConfigureAwait(true);
                var omittedDependency = await RunPowerShell(
                    validator,
                    [
                        "-PackagePath", omittedDependencyPackage,
                    "-PackageKind", "zip",
                    "-RuntimeIdentifier", "win-x64",
                    "-ExpectedManifestPath", expectedManifest,
                    "-OutputPath", Path.Combine(root, "omitted-dependency-manifest.json")
                    ],
                    root).ConfigureAwait(false);
                Assert.NotEqual(0, omittedDependency.ExitCode);
                Assert.Contains("does not match the validated publish manifest", NormalizeDiagnostic(omittedDependency), StringComparison.Ordinal);

                WriteNonEmptyFile(desktopDependency);
                await File.AppendAllTextAsync(
                    desktopDependency,
                    "corrupted",
                    TestContext.Current.CancellationToken).ConfigureAwait(true);
                var corruptedDependencyPackage = Path.Combine(root, "corrupted-dependency.zip");
                await ZipFile.CreateFromDirectoryAsync(
                    runtime,
                    corruptedDependencyPackage,
                    TestContext.Current.CancellationToken).ConfigureAwait(true);
                var corruptedDependency = await RunPowerShell(
                    validator,
                    [
                        "-PackagePath", corruptedDependencyPackage,
                    "-PackageKind", "zip",
                    "-RuntimeIdentifier", "win-x64",
                    "-ExpectedManifestPath", expectedManifest,
                    "-OutputPath", Path.Combine(root, "corrupted-dependency-manifest.json")
                    ],
                    root).ConfigureAwait(false);
                Assert.NotEqual(0, corruptedDependency.ExitCode);
                Assert.Contains("does not match the validated publish manifest", NormalizeDiagnostic(corruptedDependency), StringComparison.Ordinal);
                WriteNonEmptyFile(desktopDependency);

                await File.AppendAllTextAsync(
                    ffmpeg,
                    "substituted",
                    TestContext.Current.CancellationToken).ConfigureAwait(true);
                var substitutedPackage = Path.Combine(root, "substituted.zip");
                await ZipFile.CreateFromDirectoryAsync(
                    runtime,
                    substitutedPackage,
                    TestContext.Current.CancellationToken).ConfigureAwait(true);
                var substituted = await RunPowerShell(
                    validator,
                    [
                        "-PackagePath", substitutedPackage,
                    "-PackageKind", "zip",
                    "-RuntimeIdentifier", "win-x64",
                    "-ExpectedManifestPath", expectedManifest,
                    "-OutputPath", Path.Combine(root, "substituted-manifest.json")
                    ],
                    root).ConfigureAwait(false);
                Assert.NotEqual(0, substituted.ExitCode);
                Assert.Contains("does not match the validated publish manifest", NormalizeDiagnostic(substituted), StringComparison.Ordinal);
                WritePeFile(ffmpeg, 0x8664);

                WritePeFile(aria, 0x014c);
                var wrongArchitecturePackage = Path.Combine(root, "wrong-architecture.zip");
                await ZipFile.CreateFromDirectoryAsync(
                    runtime,
                    wrongArchitecturePackage,
                    TestContext.Current.CancellationToken).ConfigureAwait(true);
                var wrongArchitecture = await RunPowerShell(
                    validator,
                    [
                        "-PackagePath", wrongArchitecturePackage,
                    "-PackageKind", "zip",
                    "-RuntimeIdentifier", "win-x64",
                    "-ExpectedManifestPath", expectedManifest,
                    "-OutputPath", Path.Combine(root, "wrong-architecture-manifest.json")
                    ],
                    root).ConfigureAwait(false);
                Assert.NotEqual(0, wrongArchitecture.ExitCode);
                Assert.Contains("does not match win-x64", NormalizeDiagnostic(wrongArchitecture), StringComparison.Ordinal);
                WritePeFile(aria, 0x8664);
                ariaHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    await File.ReadAllBytesAsync(aria, TestContext.Current.CancellationToken).ConfigureAwait(true)));
                await File.WriteAllTextAsync(
                    Path.Combine(runtime, "aria2", "aria2c.exe.sha256"),
                    ariaHash,
                    TestContext.Current.CancellationToken).ConfigureAwait(true);

                File.Delete(Path.Combine(runtime, "Avalonia.Themes.Fluent.dll"));
                var mutatedPackage = Path.Combine(root, "mutated.zip");
                await ZipFile.CreateFromDirectoryAsync(
                    runtime,
                    mutatedPackage,
                    TestContext.Current.CancellationToken).ConfigureAwait(true);
                var mutated = await RunPowerShell(
                    validator,
                    [
                        "-PackagePath", mutatedPackage,
                    "-PackageKind", "zip",
                    "-RuntimeIdentifier", "win-x64",
                    "-ExpectedManifestPath", expectedManifest,
                    "-OutputPath", Path.Combine(root, "mutated-manifest.json")
                    ],
                    root).ConfigureAwait(false);
                Assert.NotEqual(0, mutated.ExitCode);
                Assert.Contains("Avalonia Fluent theme assembly", NormalizeDiagnostic(mutated), StringComparison.Ordinal);

                WriteNonEmptyFile(Path.Combine(runtime, "Avalonia.Themes.Fluent.dll"));
                File.Copy(typeof(string).Assembly.Location, Path.Combine(runtime, "DownKyi.dll"), overwrite: true);
                var wrongVersionPackage = Path.Combine(root, "wrong-version.zip");
                await ZipFile.CreateFromDirectoryAsync(
                    runtime,
                    wrongVersionPackage,
                    TestContext.Current.CancellationToken).ConfigureAwait(true);
                var wrongVersion = await RunPowerShell(
                    validator,
                    [
                        "-PackagePath", wrongVersionPackage,
                    "-PackageKind", "zip",
                    "-RuntimeIdentifier", "win-x64",
                    "-ExpectedManifestPath", expectedManifest,
                    "-OutputPath", Path.Combine(root, "wrong-version-manifest.json")
                    ],
                    root).ConfigureAwait(false);
                Assert.NotEqual(0, wrongVersion.ExitCode);
                Assert.Contains("does not match expected version", NormalizeDiagnostic(wrongVersion), StringComparison.Ordinal);
            },
            () => DeleteTemporaryDirectoryAsync(root)).ConfigureAwait(true);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "xUnit",
        "xUnit1013:Public method should be marked as test")]
    public static async Task LinuxReleasePackageValidationRejectsMissingExecuteBits()
    {
        var root = CreateTemporaryDirectory();
        await ExternalProcessTestHarness.RunWithCleanupAsync(
            async () =>
            {
                var package = await CreateLinuxDebPackageAsync(root, "amd64", includeExecuteBits: false).ConfigureAwait(false);
                var result = await RunPowerShell(
                    Path.Combine(RepositoryRoot, "script", "validate-v113-release-package.ps1"),
                    [
                        "-PackagePath", package,
                    "-PackageKind", "deb",
                    "-RuntimeIdentifier", "linux-x64",
                    "-ExpectedManifestPath", package + ".expected.json",
                    "-OutputPath", Path.Combine(root, "permission-manifest.json")
                    ],
                    root).ConfigureAwait(false);

                Assert.NotEqual(0, result.ExitCode);
                Assert.Contains("not executable by a non-owner", NormalizeDiagnostic(result), StringComparison.Ordinal);
            },
            () => DeleteTemporaryDirectoryAsync(root)).ConfigureAwait(true);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "xUnit",
        "xUnit1013:Public method should be marked as test")]
    public static async Task LinuxReleasePackageValidationRejectsArchitectureMismatch()
    {
        var root = CreateTemporaryDirectory();
        await ExternalProcessTestHarness.RunWithCleanupAsync(
            async () =>
            {
                var package = await CreateLinuxDebPackageAsync(root, "amd64", includeExecuteBits: true).ConfigureAwait(false);
                var result = await RunPowerShell(
                    Path.Combine(RepositoryRoot, "script", "validate-v113-release-package.ps1"),
                    [
                        "-PackagePath", package,
                    "-PackageKind", "deb",
                    "-RuntimeIdentifier", "linux-arm64",
                    "-ExpectedManifestPath", package + ".expected.json",
                    "-OutputPath", Path.Combine(root, "architecture-manifest.json")
                    ],
                    root).ConfigureAwait(false);

                Assert.NotEqual(0, result.ExitCode);
                Assert.Contains("architecture amd64 does not match linux-arm64", NormalizeDiagnostic(result), StringComparison.Ordinal);
            },
            () => DeleteTemporaryDirectoryAsync(root)).ConfigureAwait(true);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "xUnit",
        "xUnit1013:Public method should be marked as test")]
    public static async Task LinuxReleasePackageValidationRejectsOwnerOnlyExecuteBits()
    {
        var root = CreateTemporaryDirectory();
        await ExternalProcessTestHarness.RunWithCleanupAsync(
            async () =>
            {
                var package = await CreateLinuxDebPackageAsync(
                    root,
                    "amd64",
                    includeExecuteBits: true,
                    ownerOnlyExecute: true).ConfigureAwait(false);
                var result = await RunPowerShell(
                    Path.Combine(RepositoryRoot, "script", "validate-v113-release-package.ps1"),
                    [
                        "-PackagePath", package,
                    "-PackageKind", "deb",
                    "-RuntimeIdentifier", "linux-x64",
                    "-ExpectedManifestPath", package + ".expected.json",
                    "-OutputPath", Path.Combine(root, "owner-only-manifest.json")
                    ],
                    root).ConfigureAwait(false);

                Assert.NotEqual(0, result.ExitCode);
                Assert.Contains("not executable by a non-owner", NormalizeDiagnostic(result), StringComparison.Ordinal);
            },
            () => DeleteTemporaryDirectoryAsync(root)).ConfigureAwait(true);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "xUnit",
        "xUnit1013:Public method should be marked as test")]
    public static async Task LinuxReleasePackageValidationRejectsCrossFormatBinary()
    {
        var root = CreateTemporaryDirectory();
        await ExternalProcessTestHarness.RunWithCleanupAsync(
            async () =>
            {
                var package = await CreateLinuxDebPackageAsync(
                    root,
                    "amd64",
                    includeExecuteBits: true,
                    crossFormatExecutable: true).ConfigureAwait(false);
                var result = await RunPowerShell(
                    Path.Combine(RepositoryRoot, "script", "validate-v113-release-package.ps1"),
                    [
                        "-PackagePath", package,
                    "-PackageKind", "deb",
                    "-RuntimeIdentifier", "linux-x64",
                    "-ExpectedManifestPath", package + ".expected.json",
                    "-OutputPath", Path.Combine(root, "cross-format-manifest.json")
                    ],
                    root).ConfigureAwait(false);

                Assert.NotEqual(0, result.ExitCode);
                Assert.Contains("does not match linux-x64", NormalizeDiagnostic(result), StringComparison.Ordinal);
            },
            () => DeleteTemporaryDirectoryAsync(root)).ConfigureAwait(true);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "xUnit",
        "xUnit1013:Public method should be marked as test")]
    public static async Task LinuxReleasePackageValidationRejectsMixedElfArchitectures()
    {
        var root = CreateTemporaryDirectory();
        await ExternalProcessTestHarness.RunWithCleanupAsync(
            async () =>
            {
                var package = await CreateLinuxDebPackageAsync(
                    root,
                    "amd64",
                    includeExecuteBits: true,
                    mixedArchitectureLibrary: true).ConfigureAwait(false);
                var result = await RunPowerShell(
                    Path.Combine(RepositoryRoot, "script", "validate-v113-release-package.ps1"),
                    [
                        "-PackagePath", package,
                    "-PackageKind", "deb",
                    "-RuntimeIdentifier", "linux-x64",
                    "-ExpectedManifestPath", package + ".expected.json",
                    "-OutputPath", Path.Combine(root, "mixed-elf-manifest.json")
                    ],
                    root).ConfigureAwait(false);

                Assert.NotEqual(0, result.ExitCode);
                Assert.Contains("wrong-architecture-fixture.so", NormalizeDiagnostic(result), StringComparison.Ordinal);
                Assert.Contains("does not match linux-x64", NormalizeDiagnostic(result), StringComparison.Ordinal);
            },
            () => DeleteTemporaryDirectoryAsync(root)).ConfigureAwait(true);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "xUnit",
        "xUnit1013:Public method should be marked as test")]
    public static async Task LinuxReleasePackageValidationRejectsMissingAppImageEntrypoint()
    {
        var root = CreateTemporaryDirectory();
        await ExternalProcessTestHarness.RunWithCleanupAsync(
            async () =>
            {
                var validator = Path.Combine(RepositoryRoot, "script", "validate-v113-release-package.ps1");
                var brokenAppRunFixture = await CreateLinuxAppImageFixtureAsync(
                    Path.Combine(root, "broken-app-run"),
                    AppRunFixtureKind.RegularExitsImmediately).ConfigureAwait(false);
                var brokenAppRun = await RunPowerShell(
                    validator,
                    [
                        "-PackagePath", brokenAppRunFixture.Package,
                    "-PackageKind", "AppImage",
                    "-RuntimeIdentifier", "linux-x64",
                    "-ExpectedManifestPath", brokenAppRunFixture.ExpectedManifest,
                    "-OutputPath", Path.Combine(root, "broken-app-run-manifest.json")
                    ],
                    root).ConfigureAwait(false);
                Assert.NotEqual(0, brokenAppRun.ExitCode);
                Assert.Contains("AppRun launch smoke exited", NormalizeDiagnostic(brokenAppRun), StringComparison.Ordinal);

                var wrongStubFixture = await CreateLinuxAppImageFixtureAsync(
                    Path.Combine(root, "wrong-stub"),
                    AppRunFixtureKind.ValidSymlink,
                    outerRuntimeStaysRunning: false).ConfigureAwait(false);
                var wrongStub = await RunPowerShell(
                    validator,
                    [
                        "-PackagePath", wrongStubFixture.Package,
                    "-PackageKind", "AppImage",
                    "-RuntimeIdentifier", "linux-x64",
                    "-ExpectedManifestPath", wrongStubFixture.ExpectedManifest,
                    "-OutputPath", Path.Combine(root, "wrong-stub-manifest.json")
                    ],
                    root).ConfigureAwait(false);
                Assert.NotEqual(0, wrongStub.ExitCode);
                Assert.Contains("AppImage runtime launch smoke exited", NormalizeDiagnostic(wrongStub), StringComparison.Ordinal);

                var mutatedFixture = await CreateLinuxAppImageFixtureAsync(
                    Path.Combine(root, "missing"),
                    AppRunFixtureKind.Missing).ConfigureAwait(false);
                var mutated = await RunPowerShell(
                    validator,
                    [
                        "-PackagePath", mutatedFixture.Package,
                    "-PackageKind", "AppImage",
                    "-RuntimeIdentifier", "linux-x64",
                    "-ExpectedManifestPath", mutatedFixture.ExpectedManifest,
                    "-OutputPath", Path.Combine(root, "missing-appimage-manifest.json")
                    ],
                    root).ConfigureAwait(false);
                Assert.NotEqual(0, mutated.ExitCode);
                Assert.Contains("entrypoint AppRun is missing", NormalizeDiagnostic(mutated), StringComparison.Ordinal);

                var missingTargetFixture = await CreateLinuxAppImageFixtureAsync(
                    Path.Combine(root, "missing-target"),
                    AppRunFixtureKind.MissingTarget).ConfigureAwait(false);
                var missingTarget = await RunPowerShell(
                    validator,
                    [
                        "-PackagePath", missingTargetFixture.Package,
                    "-PackageKind", "AppImage",
                    "-RuntimeIdentifier", "linux-x64",
                    "-ExpectedManifestPath", missingTargetFixture.ExpectedManifest,
                    "-OutputPath", Path.Combine(root, "missing-target-manifest.json")
                    ],
                    root).ConfigureAwait(false);
                Assert.NotEqual(0, missingTarget.ExitCode);
                Assert.Contains("symlink target does not exist", NormalizeDiagnostic(missingTarget), StringComparison.Ordinal);

                var wrongTargetFixture = await CreateLinuxAppImageFixtureAsync(
                    Path.Combine(root, "wrong-target"),
                    AppRunFixtureKind.WrongTarget).ConfigureAwait(false);
                var wrongTarget = await RunPowerShell(
                    validator,
                    [
                        "-PackagePath", wrongTargetFixture.Package,
                    "-PackageKind", "AppImage",
                    "-RuntimeIdentifier", "linux-x64",
                    "-ExpectedManifestPath", wrongTargetFixture.ExpectedManifest,
                    "-OutputPath", Path.Combine(root, "wrong-target-manifest.json")
                    ],
                    root).ConfigureAwait(false);
                Assert.NotEqual(0, wrongTarget.ExitCode);
                Assert.Contains("symlink target is incorrect", NormalizeDiagnostic(wrongTarget), StringComparison.Ordinal);

                var nonExecutableTargetFixture = await CreateLinuxAppImageFixtureAsync(
                    Path.Combine(root, "non-executable-target"),
                    AppRunFixtureKind.NonExecutableTarget).ConfigureAwait(false);
                var nonExecutableTarget = await RunPowerShell(
                    validator,
                    [
                        "-PackagePath", nonExecutableTargetFixture.Package,
                    "-PackageKind", "AppImage",
                    "-RuntimeIdentifier", "linux-x64",
                    "-ExpectedManifestPath", nonExecutableTargetFixture.ExpectedManifest,
                    "-OutputPath", Path.Combine(root, "non-executable-target-manifest.json")
                    ],
                    root).ConfigureAwait(false);
                Assert.NotEqual(0, nonExecutableTarget.ExitCode);
                Assert.Contains("symlink target is not executable", NormalizeDiagnostic(nonExecutableTarget), StringComparison.Ordinal);

                var validFixture = await CreateLinuxAppImageFixtureAsync(
                    Path.Combine(root, "valid"),
                    AppRunFixtureKind.ValidSymlink).ConfigureAwait(false);
                var valid = await RunPowerShell(
                    validator,
                    [
                        "-PackagePath", validFixture.Package,
                    "-PackageKind", "AppImage",
                    "-RuntimeIdentifier", "linux-x64",
                    "-ExpectedManifestPath", validFixture.ExpectedManifest,
                    "-OutputPath", Path.Combine(root, "valid-appimage-manifest.json")
                    ],
                    root).ConfigureAwait(false);
                Assert.Equal(0, valid.ExitCode);
            },
            () => DeleteTemporaryDirectoryAsync(root)).ConfigureAwait(true);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "xUnit",
        "xUnit1013:Public method should be marked as test")]
    public static async Task LinuxReleasePackageValidationRejectsPackageManagerVersionMismatch()
    {
        var root = CreateTemporaryDirectory();
        await ExternalProcessTestHarness.RunWithCleanupAsync(
            async () =>
            {
                var validator = Path.Combine(RepositoryRoot, "script", "validate-v113-release-package.ps1");
                var debPackage = await CreateLinuxDebPackageAsync(
                    root,
                    "amd64",
                    includeExecuteBits: true,
                    version: "1.1.2").ConfigureAwait(false);
                var debResult = await RunPowerShell(
                    validator,
                    [
                        "-PackagePath", debPackage,
                    "-PackageKind", "deb",
                    "-RuntimeIdentifier", "linux-x64",
                    "-ExpectedManifestPath", debPackage + ".expected.json",
                    "-OutputPath", Path.Combine(root, "deb-version-manifest.json")
                    ],
                    root).ConfigureAwait(false);
                Assert.NotEqual(0, debResult.ExitCode);
                Assert.Contains("package version 1.1.2-1 does not match 1.1.3-1", NormalizeDiagnostic(debResult), StringComparison.Ordinal);

                var rpmPackage = await CreateLinuxRpmPackageAsync(root, "x86_64", "1.1.2").ConfigureAwait(false);
                var rpmResult = await RunPowerShell(
                    validator,
                    [
                        "-PackagePath", rpmPackage,
                    "-PackageKind", "rpm",
                    "-RuntimeIdentifier", "linux-x64",
                    "-ExpectedManifestPath", rpmPackage + ".expected.json",
                    "-OutputPath", Path.Combine(root, "rpm-version-manifest.json")
                    ],
                    root).ConfigureAwait(false);
                Assert.NotEqual(0, rpmResult.ExitCode);
                Assert.Contains("package EVR 0:1.1.2-1 does not match 0:1.1.3-1", NormalizeDiagnostic(rpmResult), StringComparison.Ordinal);
            },
            () => DeleteTemporaryDirectoryAsync(root)).ConfigureAwait(true);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "xUnit",
        "xUnit1013:Public method should be marked as test")]
    public static async Task LinuxReleasePackageValidationRejectsRpmEvrMismatch()
    {
        var root = CreateTemporaryDirectory();
        await ExternalProcessTestHarness.RunWithCleanupAsync(
            async () =>
            {
                var validator = Path.Combine(RepositoryRoot, "script", "validate-v113-release-package.ps1");
                var releasePackage = await CreateLinuxRpmPackageAsync(root, "x86_64", "1.1.3", release: "2").ConfigureAwait(false);
                var releaseResult = await RunPowerShell(
                    validator,
                    [
                        "-PackagePath", releasePackage,
                    "-PackageKind", "rpm",
                    "-RuntimeIdentifier", "linux-x64",
                    "-ExpectedManifestPath", releasePackage + ".expected.json",
                    "-OutputPath", Path.Combine(root, "rpm-release-manifest.json")
                    ],
                    root).ConfigureAwait(false);
                Assert.NotEqual(0, releaseResult.ExitCode);
                Assert.Contains("package EVR 0:1.1.3-2 does not match 0:1.1.3-1", NormalizeDiagnostic(releaseResult), StringComparison.Ordinal);

                var epochPackage = await CreateLinuxRpmPackageAsync(root, "x86_64", "1.1.3", epoch: 1).ConfigureAwait(false);
                var epochResult = await RunPowerShell(
                    validator,
                    [
                        "-PackagePath", epochPackage,
                    "-PackageKind", "rpm",
                    "-RuntimeIdentifier", "linux-x64",
                    "-ExpectedManifestPath", epochPackage + ".expected.json",
                    "-OutputPath", Path.Combine(root, "rpm-epoch-manifest.json")
                    ],
                    root).ConfigureAwait(false);
                Assert.NotEqual(0, epochResult.ExitCode);
                Assert.Contains("package EVR 1:1.1.3-1 does not match 0:1.1.3-1", NormalizeDiagnostic(epochResult), StringComparison.Ordinal);
            },
            () => DeleteTemporaryDirectoryAsync(root)).ConfigureAwait(true);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "xUnit",
        "xUnit1013:Public method should be marked as test")]
    public static async Task LinuxReleasePackageValidationRejectsPackageManagerIdentityMismatch()
    {
        var root = CreateTemporaryDirectory();
        await ExternalProcessTestHarness.RunWithCleanupAsync(
            async () =>
            {
                var validator = Path.Combine(RepositoryRoot, "script", "validate-v113-release-package.ps1");
                var debPackage = await CreateLinuxDebPackageAsync(
                    root,
                    "amd64",
                    includeExecuteBits: true,
                    packageName: "downkyi-fixture").ConfigureAwait(false);
                var debResult = await RunPowerShell(
                    validator,
                    [
                        "-PackagePath", debPackage,
                    "-PackageKind", "deb",
                    "-RuntimeIdentifier", "linux-x64",
                    "-ExpectedManifestPath", debPackage + ".expected.json",
                    "-OutputPath", Path.Combine(root, "deb-identity-manifest.json")
                    ],
                    root).ConfigureAwait(false);
                Assert.NotEqual(0, debResult.ExitCode);
                Assert.Contains("package identity downkyi-fixture does not match downkyi", NormalizeDiagnostic(debResult), StringComparison.Ordinal);

                var rpmPackage = await CreateLinuxRpmPackageAsync(root, "x86_64", "1.1.3", "downkyi-fixture").ConfigureAwait(false);
                var rpmResult = await RunPowerShell(
                    validator,
                    [
                        "-PackagePath", rpmPackage,
                    "-PackageKind", "rpm",
                    "-RuntimeIdentifier", "linux-x64",
                    "-ExpectedManifestPath", rpmPackage + ".expected.json",
                    "-OutputPath", Path.Combine(root, "rpm-identity-manifest.json")
                    ],
                    root).ConfigureAwait(false);
                Assert.NotEqual(0, rpmResult.ExitCode);
                Assert.Contains("package identity downkyi-fixture does not match downkyi", NormalizeDiagnostic(rpmResult), StringComparison.Ordinal);
            },
            () => DeleteTemporaryDirectoryAsync(root)).ConfigureAwait(true);
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

    private static void WriteElfFile(string path, ushort machine)
    {
        var image = new byte[64];
        image[0] = 0x7f;
        image[1] = 0x45;
        image[2] = 0x4c;
        image[3] = 0x46;
        image[4] = 2;
        image[5] = 1;
        image[6] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(16), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(18), machine);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20), 1);
        File.WriteAllBytes(path, image);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static async Task<string> CreateLinuxDebPackageAsync(
        string root,
        string architecture,
        bool includeExecuteBits,
        bool ownerOnlyExecute = false,
        string version = "1.1.3",
        string packageName = "downkyi",
        bool crossFormatExecutable = false,
        bool mixedArchitectureLibrary = false)
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
        if (mixedArchitectureLibrary)
        {
            WriteElfFile(Path.Combine(runtime, "wrong-architecture-fixture.so"), 0x00b7);
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
        var ariaHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            await File.ReadAllBytesAsync(aria, TestContext.Current.CancellationToken).ConfigureAwait(true)));
        await File.WriteAllTextAsync(
            Path.Combine(runtime, "aria2", "aria2c.sha256"),
            ariaHash,
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(
            Path.Combine(runtime, "DownKyi.deps.json"),
            "{\"libraries\":{\"Avalonia.Themes.Fluent/fixture\":{}}}",
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        WriteNonEmptyFile(Path.Combine(runtime, "Avalonia.Themes.Fluent.dll"));
        await File.WriteAllTextAsync(
            Path.Combine(controlDirectory, "control"),
            $"Package: {packageName}\nVersion: {version}-1\nArchitecture: {architecture}\nMaintainer: fixture@example.invalid\nDescription: DownKyi release validator fixture\n",
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        var package = Path.Combine(root, $"fixture-{architecture}-{includeExecuteBits}-{ownerOnlyExecute}-{version}-{packageName}.deb");
        await WriteExpectedManifest(
            runtime,
            architecture == "amd64" ? "linux-x64" : "linux-arm64",
            package + ".expected.json",
            root).ConfigureAwait(true);
        await RunRequired(
            "dpkg-deb",
            ["--root-owner-group", "--build", packageRoot, package],
            root).ConfigureAwait(true);
        return package;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static async Task<string> CreateLinuxRpmPackageAsync(
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
        var ariaHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            await File.ReadAllBytesAsync(aria, TestContext.Current.CancellationToken).ConfigureAwait(true)));
        await File.WriteAllTextAsync(
            Path.Combine(runtime, "aria2", "aria2c.sha256"),
            ariaHash,
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(
            Path.Combine(runtime, "DownKyi.deps.json"),
            "{\"libraries\":{\"Avalonia.Themes.Fluent/fixture\":{}}}",
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        WriteNonEmptyFile(Path.Combine(runtime, "Avalonia.Themes.Fluent.dll"));

        var specPath = Path.Combine(topDirectory, "SPECS", "downkyi-fixture.spec");
        var epochLine = epoch.HasValue ? $"Epoch: {epoch.Value}\n" : string.Empty;
        await File.WriteAllTextAsync(
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
            """,
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        await RunRequired(
            "rpmbuild",
            ["-bb", "--define", $"_topdir {topDirectory}", specPath],
            root).ConfigureAwait(true);
        var package = Directory.GetFiles(Path.Combine(topDirectory, "RPMS", architecture), "*.rpm").Single();
        await WriteExpectedManifest(runtime, "linux-x64", package + ".expected.json", root).ConfigureAwait(true);
        return package;
    }

    private enum AppRunFixtureKind
    {
        Missing,
        RegularExitsImmediately,
        ValidSymlink,
        MissingTarget,
        WrongTarget,
        NonExecutableTarget
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static async Task<(string Package, string ExpectedManifest)> CreateLinuxAppImageFixtureAsync(
        string root,
        AppRunFixtureKind appRunKind,
        bool outerRuntimeStaysRunning = true)
    {
        Directory.CreateDirectory(root);
        var appRoot = Path.Combine(root, "app-root");
        var runtime = Path.Combine(appRoot, "usr", "bin");
        Directory.CreateDirectory(Path.Combine(runtime, "aria2"));
        Directory.CreateDirectory(Path.Combine(runtime, "ffmpeg"));

        File.Copy(typeof(V113ReleaseSafetyRegressionTests).Assembly.Location, Path.Combine(runtime, "DownKyi.dll"));
        var downKyiSource = Path.Combine(root, "downkyi-fixture.c");
        await File.WriteAllTextAsync(
            downKyiSource,
            "#include <unistd.h>\nint main(void) { sleep(30); return 0; }\n",
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        await RunRequired(
            "gcc",
            ["-O2", "-o", Path.Combine(runtime, "DownKyi"), downKyiSource],
            root).ConfigureAwait(true);
        var aria = Path.Combine(runtime, "aria2", "aria2c");
        File.Copy("/bin/true", aria);
        File.Copy("/bin/true", Path.Combine(runtime, "ffmpeg", "ffmpeg"));
        File.Copy("/bin/true", Path.Combine(runtime, "ffmpeg", "ffprobe"));
        var ariaHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            await File.ReadAllBytesAsync(aria, TestContext.Current.CancellationToken).ConfigureAwait(true)));
        await File.WriteAllTextAsync(
            Path.Combine(runtime, "aria2", "aria2c.sha256"),
            ariaHash,
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(
            Path.Combine(runtime, "DownKyi.deps.json"),
            "{\"libraries\":{\"Avalonia.Themes.Fluent/fixture\":{}}}",
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        WriteNonEmptyFile(Path.Combine(runtime, "Avalonia.Themes.Fluent.dll"));

        var expectedManifest = Path.Combine(root, "appimage-expected.json");
        await WriteExpectedManifest(runtime, "linux-x64", expectedManifest, root).ConfigureAwait(true);

        var appRun = Path.Combine(appRoot, "AppRun");
        switch (appRunKind)
        {
            case AppRunFixtureKind.RegularExitsImmediately:
                await File.WriteAllTextAsync(
                    appRun,
                    "#!/bin/sh\nexit 0\n",
                    TestContext.Current.CancellationToken).ConfigureAwait(true);
                File.SetUnixFileMode(
                    appRun,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                break;
            case AppRunFixtureKind.ValidSymlink:
                File.CreateSymbolicLink(appRun, "usr/bin/DownKyi");
                break;
            case AppRunFixtureKind.MissingTarget:
                File.Delete(Path.Combine(runtime, "DownKyi"));
                File.CreateSymbolicLink(appRun, "usr/bin/DownKyi");
                break;
            case AppRunFixtureKind.WrongTarget:
                File.CreateSymbolicLink(appRun, "usr/bin/OtherApp");
                break;
            case AppRunFixtureKind.NonExecutableTarget:
                File.SetUnixFileMode(
                    Path.Combine(runtime, "DownKyi"),
                    UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                File.CreateSymbolicLink(appRun, "usr/bin/DownKyi");
                break;
            case AppRunFixtureKind.Missing:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(appRunKind));
        }

        var escapedAppRoot = appRoot.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        var outerBehavior = outerRuntimeStaysRunning ? "sleep(30); return 0;" : "return 0;";
        var runtimeSource = """
            #include <stdio.h>
            #include <stdlib.h>
            #include <string.h>
            #include <sys/stat.h>
            #include <unistd.h>

            int main(int argc, char **argv) {
                if (argc > 1 && strcmp(argv[1], "--appimage-extract") == 0) {
                    if (mkdir("squashfs-root", 0755) != 0) return 2;
                    char command[8192];
                    int length = snprintf(command, sizeof(command), "cp -a -- \"%s/.\" squashfs-root/", "__APP_ROOT__");
                    if (length < 0 || length >= (int)sizeof(command)) return 3;
                    return system(command) == 0 ? 0 : 4;
                }
                if (argc > 1 && strcmp(argv[1], "--appimage-extract-and-run") == 0) {
                    __OUTER_BEHAVIOR__
                }
                return 1;
            }
            """
            .Replace("__APP_ROOT__", escapedAppRoot, StringComparison.Ordinal)
            .Replace("__OUTER_BEHAVIOR__", outerBehavior, StringComparison.Ordinal);
        var runtimeSourcePath = Path.Combine(root, "appimage-runtime-fixture.c");
        await File.WriteAllTextAsync(
            runtimeSourcePath,
            runtimeSource,
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        var package = Path.Combine(root, appRunKind == AppRunFixtureKind.Missing ? "missing-app-run.AppImage" : "fixture.AppImage");
        await RunRequired("gcc", ["-O2", "-o", package, runtimeSourcePath], root).ConfigureAwait(true);
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

    private static async Task WriteExpectedManifest(
        string runtime,
        string runtimeIdentifier,
        string outputPath,
        string workingDirectory)
    {
        var result = await RunPowerShell(
            Path.Combine(RepositoryRoot, "script", "validate-publish-output.ps1"),
            [
                "-PublishDirectory", runtime,
                "-RuntimeIdentifier", runtimeIdentifier,
                "-ExpectedVersion", "1.1.3",
                "-OutputPath", outputPath
            ],
            workingDirectory).ConfigureAwait(true);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to create expected publish manifest: {NormalizeDiagnostic(result)}");
        }
    }

    private static int CountOccurrences(string source, string value) =>
        source.Split(value, StringSplitOptions.None).Length - 1;

    private static void AssertArm64PromotionContract(string workflow)
    {
        var buildLinux = GetWorkflowJob(workflow, "build-linux");
        var validateArm64 = GetWorkflowJob(workflow, "validate-linux-arm64");
        var release = GetWorkflowJob(workflow, "release");

        Assert.Contains(
            "name: linux-arm64-${{ matrix.kind }}-candidate",
            buildLinux,
            StringComparison.Ordinal);
        Assert.Contains(
            "path: linux-arm64-${{ matrix.kind }}.candidate.internal.transport.tar",
            buildLinux,
            StringComparison.Ordinal);
        Assert.DoesNotContain("name: appimage-arm64-transport", buildLinux, StringComparison.Ordinal);
        Assert.DoesNotContain("name: downkyi_${{ steps.version.outputs.content }}_linux_self-contained_arm64.deb", buildLinux, StringComparison.Ordinal);

        Assert.Contains("needs: build-linux", validateArm64, StringComparison.Ordinal);
        Assert.Contains("name: linux-arm64-${{ matrix.kind }}-candidate", validateArm64, StringComparison.Ordinal);
        Assert.Contains("name: appimage-arm64-transport", validateArm64, StringComparison.Ordinal);
        Assert.Contains(
            "name: downkyi_${{ steps.version.outputs.content }}_linux_self-contained_arm64.deb",
            validateArm64,
            StringComparison.Ordinal);

        Assert.Contains(
            "needs: [changelog, build-windows, build-linux, validate-linux-arm64, build-macos]",
            release,
            StringComparison.Ordinal);
        Assert.Contains(
            "Get-ChildItem artifacts -File -Filter '*.internal.transport.tar'",
            release,
            StringComparison.Ordinal);
        Assert.Contains(
            "Get-ChildItem artifacts -Recurse -File -Filter '*.internal.*'",
            release,
            StringComparison.Ordinal);
    }

    private static string GetWorkflowJob(string workflow, string jobName)
    {
        var normalized = workflow.Replace("\r\n", "\n", StringComparison.Ordinal);
        var marker = $"\n  {jobName}:\n";
        var start = normalized.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Workflow job was not found: {jobName}");
        start += 1;
        var remainder = normalized[(start + marker.Length - 1)..];
        var nextJob = System.Text.RegularExpressions.Regex.Match(
            remainder,
            @"\n  [A-Za-z0-9_-]+:\n",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return nextJob.Success
            ? normalized[start..(start + marker.Length - 1 + nextJob.Index)]
            : normalized[start..];
    }

    private static string NormalizeDiagnostic(ExternalProcessResult result) =>
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

    private static Task DeleteTemporaryDirectoryAsync(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
        }
        Directory.Delete(path, recursive: true);
        return Task.CompletedTask;
    }

    private static async Task<ExternalProcessResult> RunPowerShell(
        string script,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var allArguments = new List<string> { "-NoLogo", "-NoProfile", "-File", script };
        allArguments.AddRange(arguments);
        return await RunProcess("pwsh", allArguments, workingDirectory, environment).ConfigureAwait(true);
    }

    private static async Task<ExternalProcessResult> RunRequired(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        var result = await RunProcess(executable, arguments, workingDirectory).ConfigureAwait(true);
        Assert.True(result.ExitCode == 0, $"{executable} failed: {result.StandardError}");
        return result;
    }

    private static async Task<ExternalProcessResult> RunProcess(
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

        return await ExternalProcessTestHarness.RunAsync(
            startInfo,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
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

}
