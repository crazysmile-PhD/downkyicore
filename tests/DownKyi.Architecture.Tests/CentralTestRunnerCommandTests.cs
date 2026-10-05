using DownKyi.CentralTestRunner;

namespace DownKyi.Architecture.Tests;

public sealed class CentralTestRunnerCommandTests
{
    [Fact]
    public void CommandOptionsRejectsRemovedPerProjectTimeout()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => CommandOptions.Parse(["--timeout-seconds", "300"]));

        Assert.Contains("Unknown option", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandOptionsCollectsExcludedClasses()
    {
        var options = CommandOptions.Parse(
            ["--exclude-class", "Fixture.Tests.B", "--exclude-class", "Fixture.Tests.A"]);

        Assert.Equal(["Fixture.Tests.B", "Fixture.Tests.A"], options.ExcludedClasses);
    }

    [Fact]
    public void VstestInvocationExcludesClassesAfterApplyingTheSelectedClassFilter()
    {
        var options = CommandOptions.Parse(
            [
                "--class", "Fixture.Tests.Included",
                "--exclude-class", "Fixture.Tests.ExcludedB",
                "--exclude-class", "Fixture.Tests.ExcludedA"
            ]);

        var startInfo = TestInvocationFactory.CreateVstestStartInfo(
            "fixture.csproj",
            options,
            resultsDirectory: null,
            trxName: "fixture.trx");
        var arguments = startInfo.ArgumentList.ToArray();
        var filterIndex = Array.IndexOf(arguments, "--filter");

        Assert.True(filterIndex >= 0);
        Assert.Equal(
            "(FullyQualifiedName~Fixture.Tests.Included)&" +
            "FullyQualifiedName!~Fixture.Tests.ExcludedA&" +
            "FullyQualifiedName!~Fixture.Tests.ExcludedB",
            arguments[filterIndex + 1]);
    }

    [Fact]
    public void InProcessXunitInvocationUsesNativeClassExclusions()
    {
        var options = CommandOptions.Parse(
            ["--exclude-class", "Fixture.Tests.ExcludedB", "--exclude-class", "Fixture.Tests.ExcludedA"]);
        var projectDirectory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-in-process-exclusion-{Guid.NewGuid():N}");
        const string targetFramework = "fixture-tfm";
        var assemblyDirectory = Path.Combine(projectDirectory, "bin", "Release", targetFramework);
        Directory.CreateDirectory(assemblyDirectory);
        File.WriteAllText(Path.Combine(assemblyDirectory, "Fixture.Tests.dll"), string.Empty);
        try
        {
            var startInfo = TestInvocationFactory.CreateInProcessXunitStartInfo(
                Path.Combine(projectDirectory, "Fixture.Tests.csproj"),
                targetFramework,
                options,
                trxPath: null);
            var arguments = startInfo.ArgumentList.ToArray();

            Assert.Equal(2, arguments.Count(argument => argument == "-class-"));
            Assert.Contains("Fixture.Tests.ExcludedA", arguments);
            Assert.Contains("Fixture.Tests.ExcludedB", arguments);
        }
        finally
        {
            Directory.Delete(projectDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task RunSolutionRejectsEmptyProjectDiscovery()
    {
        var repositoryRoot = await CreateRepositoryAsync();
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => RunSolutionAsync(repositoryRoot));

            Assert.Contains("No runnable test projects", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RunSolutionRejectsEmptyCurrentPlatformSelection()
    {
        var repositoryRoot = await CreateRepositoryAsync();
        try
        {
            var unsupportedPlatform = OperatingSystem.IsWindows() ? "Linux" : "Windows";
            await WriteProjectAsync(repositoryRoot, "Fixture.Tests", unsupportedPlatform, failBuild: false);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => RunSolutionAsync(repositoryRoot));

            Assert.Contains("No test projects support", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RunSolutionClearsEverySelectedTrxBeforeFirstBuildFailure()
    {
        var repositoryRoot = await CreateRepositoryAsync();
        try
        {
            await WriteProjectAsync(
                repositoryRoot,
                "A.Tests",
                "Windows;Linux;macOS",
                failBuild: true);
            await WriteProjectAsync(
                repositoryRoot,
                "Z.Tests",
                "Windows;Linux;macOS",
                failBuild: false);
            var resultsDirectory = Path.Combine(repositoryRoot, "results");
            Directory.CreateDirectory(resultsDirectory);
            var firstTrx = Path.Combine(resultsDirectory, "A.Tests.trx");
            var laterTrx = Path.Combine(resultsDirectory, "Z.Tests.trx");
            await File.WriteAllTextAsync(firstTrx, "stale-pass", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(laterTrx, "stale-pass", TestContext.Current.CancellationToken);

            var exitCode = await RunSolutionAsync(repositoryRoot, resultsDirectory);

            Assert.NotEqual(0, exitCode);
            Assert.False(File.Exists(firstTrx));
            Assert.False(File.Exists(laterTrx));
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    private static Task<int> RunSolutionAsync(string repositoryRoot, string? resultsDirectory = null)
    {
        var arguments = new List<string>
        {
            "run-solution",
            "--repository-root", repositoryRoot,
            "--configuration", "Release",
            "--no-restore"
        };
        if (resultsDirectory is not null)
        {
            arguments.Add("--results-directory");
            arguments.Add(resultsDirectory);
        }

        return CentralTestCommand.RunAsync(arguments.ToArray(), TestContext.Current.CancellationToken);
    }

    private static async Task<string> CreateRepositoryAsync()
    {
        var repositoryRoot = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-central-runner-command-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(repositoryRoot, "tests"));
        var policyDirectory = Path.Combine(repositoryRoot, "docs", "testing");
        Directory.CreateDirectory(policyDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(policyDirectory, "test-runner-policy.json"),
            """{"schemaVersion":1,"projects":[]}""",
            TestContext.Current.CancellationToken).ConfigureAwait(false);
        return repositoryRoot;
    }

    private static Task WriteProjectAsync(
        string repositoryRoot,
        string projectName,
        string platforms,
        bool failBuild)
    {
        var projectDirectory = Path.Combine(repositoryRoot, "tests", projectName);
        Directory.CreateDirectory(projectDirectory);
        var buildTarget = failBuild
            ? "<Error Text=\"intentional first project failure\" />"
            : string.Empty;
        return File.WriteAllTextAsync(
            Path.Combine(projectDirectory, $"{projectName}.csproj"),
            $$"""
            <Project DefaultTargets="Build">
              <PropertyGroup>
                <DownKyiTestPlatforms>{{platforms}}</DownKyiTestPlatforms>
              </PropertyGroup>
              <Target Name="Build">
                {{buildTarget}}
              </Target>
            </Project>
            """,
            TestContext.Current.CancellationToken);
    }

}
