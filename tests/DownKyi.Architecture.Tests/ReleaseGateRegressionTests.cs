using System.Diagnostics;
using System.IO.Compression;

namespace DownKyi.Architecture.Tests;

public sealed class ReleaseGateRegressionTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void GenericReleaseWorkflowInvokesFailClosedReleaseGates()
    {
        var workflow = File.ReadAllText(Path.Combine(RepositoryRoot, ".github", "workflows", "build.yml"));

        Assert.Contains("validate-release-subject.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("resolve-macos-release-trust.ps1", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("HAS_MACOS_SIGNING: ${{ secrets.", workflow, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(workflow, "validate-release-package.ps1"));
    }

    [Fact]
    public void ReleaseTagProvenanceRejectsLightweightAndNonMainTags()
    {
        var root = CreateTemporaryDirectory();
        var remote = Path.Combine(root, "remote.git");
        var repository = Path.Combine(root, "repository");
        var validator = Path.Combine(RepositoryRoot, "script", "validate-release-subject.ps1");

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
                ["-SubjectDirectory", repository, "-ReleaseVersion", "v1.1.3", "-SubjectSha", mainCommit, "-RequireExactMain"],
                repository);
            Assert.Equal(0, valid.ExitCode);

            RunRequired("git", ["tag", "-d", "v1.1.3"], repository);
            RunRequired("git", ["tag", "v1.1.3"], repository);
            var lightweight = RunPowerShell(
                validator,
                ["-SubjectDirectory", repository, "-ReleaseVersion", "v1.1.3", "-SubjectSha", mainCommit, "-RequireExactMain"],
                repository);
            Assert.NotEqual(0, lightweight.ExitCode);
            Assert.Contains("annotated tag", lightweight.StandardError, StringComparison.OrdinalIgnoreCase);

            RunRequired("git", ["tag", "-d", "v1.1.3"], repository);
            RunRequired("git", ["tag", "-a", "v1.1.3", "-m", "v1.1.3"], repository);
            RunRequired("git", ["tag", "-a", "v1.1.4", "-m", "v1.1.4"], repository);
            var mismatchedVersion = RunPowerShell(
                validator,
                ["-SubjectDirectory", repository, "-ReleaseVersion", "v1.1.4", "-SubjectSha", mainCommit, "-RequireExactMain"],
                repository);
            Assert.NotEqual(0, mismatchedVersion.ExitCode);
            Assert.Contains("version.txt", mismatchedVersion.StandardError, StringComparison.Ordinal);
            Assert.Contains("1.1.3", mismatchedVersion.StandardError, StringComparison.Ordinal);

            RunRequired("git", ["checkout", "-b", "release-fixture"], repository);
            File.AppendAllText(Path.Combine(repository, "fixture.txt"), "\nrelease-only");
            RunRequired("git", ["add", "fixture.txt"], repository);
            RunRequired("git", ["commit", "-m", "release-only fixture"], repository);
            var releaseOnlyCommit = RunRequired("git", ["rev-parse", "HEAD"], repository).StandardOutput.Trim();
            RunRequired("git", ["tag", "-f", "-a", "v1.1.3", "-m", "v1.1.3"], repository);
            var nonMain = RunPowerShell(
                validator,
                ["-SubjectDirectory", repository, "-ReleaseVersion", "v1.1.3", "-SubjectSha", releaseOnlyCommit, "-RequireExactMain"],
                repository);
            Assert.NotEqual(0, nonMain.ExitCode);
            Assert.Contains("is not an ancestor of current main", nonMain.StandardError, StringComparison.OrdinalIgnoreCase);
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
        var resolver = Path.Combine(RepositoryRoot, "script", "resolve-macos-release-trust.ps1");

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
            Assert.Contains("Partial Apple credentials", partial.StandardError, StringComparison.Ordinal);
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
        var expectedVersion = File.ReadAllText(Path.Combine(RepositoryRoot, "version.txt")).Trim();
        var validator = Path.Combine(RepositoryRoot, "script", "validate-release-package.ps1");

        try
        {
            File.Copy(typeof(ReleaseGateRegressionTests).Assembly.Location, Path.Combine(runtime, "DownKyi.dll"));
            WriteNonEmptyFile(Path.Combine(runtime, "DownKyi.exe"));
            var aria = Path.Combine(runtime, "aria2", "aria2c.exe");
            WriteNonEmptyFile(aria);
            var ariaHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(aria)));
            File.WriteAllText(Path.Combine(runtime, "aria2", "aria2c.exe.sha256"), ariaHash);
            WriteNonEmptyFile(Path.Combine(runtime, "ffmpeg", "ffmpeg.exe"));
            WriteNonEmptyFile(Path.Combine(runtime, "ffmpeg", "ffprobe.exe"));
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
                    "-ExpectedVersion", expectedVersion,
                    "-OutputPath", Path.Combine(root, "valid-manifest.json")
                ],
                root);
            Assert.Equal(0, valid.ExitCode);

            File.Delete(Path.Combine(runtime, "Avalonia.Themes.Fluent.dll"));
            var mutatedPackage = Path.Combine(root, "mutated.zip");
            ZipFile.CreateFromDirectory(runtime, mutatedPackage);
            var mutated = RunPowerShell(
                validator,
                [
                    "-PackagePath", mutatedPackage,
                    "-PackageKind", "zip",
                    "-RuntimeIdentifier", "win-x64",
                    "-ExpectedVersion", expectedVersion,
                    "-OutputPath", Path.Combine(root, "mutated-manifest.json")
                ],
                root);
            Assert.NotEqual(0, mutated.ExitCode);
            Assert.Contains("Avalonia Fluent theme assembly", mutated.StandardError, StringComparison.Ordinal);

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
                    "-ExpectedVersion", expectedVersion,
                    "-OutputPath", Path.Combine(root, "wrong-version-manifest.json")
                ],
                root);
            Assert.NotEqual(0, wrongVersion.ExitCode);
            Assert.Contains("does not match expected version", wrongVersion.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    private static void WriteNonEmptyFile(string path) => File.WriteAllText(path, "fixture");

    private static int CountOccurrences(string source, string value) =>
        source.Split(value, StringSplitOptions.None).Length - 1;

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
