using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DownKyi.Architecture.Tests;

public sealed partial class TestPlatformOwnershipArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string[] AllowedPlatforms =
        ["Windows", "Linux", "macOS"];
    private static readonly string[] MacOnlyPlatforms = ["macOS"];
    private static readonly string[] LinuxOnlyPlatforms = ["Linux"];
    private static readonly string[] WindowsOnlyPlatforms = ["Windows"];

    [Fact]
    public void EveryRunnableTestProjectDeclaresPlatformOwnership()
    {
        var solution = File.ReadAllText(Path.Combine(RepositoryRoot, "DownKyi.sln"))
            .Replace('\\', '/');
        var projects = FindTestProjects();

        Assert.NotEmpty(projects);
        foreach (var project in projects)
        {
            var platforms = ReadDeclaredPlatforms(project);
            Assert.NotEmpty(platforms);
            Assert.All(platforms, platform => Assert.Contains(platform, AllowedPlatforms));

            var relativePath = Path.GetRelativePath(RepositoryRoot, project).Replace('\\', '/');
            Assert.Contains(relativePath, solution, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PlatformOwnershipIsEnforcedByBuildAndSharedTestRunners()
    {
        var directoryProps = Read("tests/Directory.Build.props");
        var runner = Read("script/test-project-runner.ps1");
        var compiledRunner = Read("tools/DownKyi.CentralTestRunner/CentralTestRunner.cs");
        var selectorTests = Read("script/test-platform-selector.ps1");
        var solutionRunner = Read("script/test-solution.ps1");
        var lifecycleRunner = Read("script/test-assembly-lifecycle.ps1");
        var reviewRunner = Read("script/test-review-invariants.ps1");
        var ciActionRunner = Read("script/invoke-ci-test-action.ps1");
        var delegatedScope = Read("script/delegated-cgroup-scope.ps1");

        Assert.Contains("ValidateDownKyiTestPlatformOwnership", directoryProps, StringComparison.Ordinal);
        Assert.Contains("must declare DownKyiTestPlatforms", directoryProps, StringComparison.Ordinal);
        Assert.Contains("Get-DownKyiCurrentTestPlatform", runner, StringComparison.Ordinal);
        Assert.Contains("Get-DownKyiTestProjectPlatforms", runner, StringComparison.Ordinal);
        Assert.Contains("Test-DownKyiTestProjectSupportsPlatform", runner, StringComparison.Ordinal);
        Assert.Contains("Select-DownKyiTestProjectsForCurrentPlatform", runner, StringComparison.Ordinal);
        Assert.Contains("cannot run on", compiledRunner, StringComparison.Ordinal);
        Assert.Contains("DownKyi.MacOS.Tests", selectorTests, StringComparison.Ordinal);
        Assert.Contains("DownKyi.Linux.Tests", selectorTests, StringComparison.Ordinal);
        Assert.Contains("missing ownership", selectorTests, StringComparison.Ordinal);
        Assert.Contains("unknown platform", selectorTests, StringComparison.Ordinal);
        Assert.Contains("test-platform-selector.ps1", solutionRunner, StringComparison.Ordinal);
        Assert.Contains("CentralTestPolicy.SelectProjects", compiledRunner, StringComparison.Ordinal);
        Assert.Contains("test-project-runner.ps1", lifecycleRunner, StringComparison.Ordinal);
        Assert.Contains("Select-DownKyiTestProjectsForCurrentPlatform", lifecycleRunner, StringComparison.Ordinal);
        Assert.Contains("test-project-runner.ps1", reviewRunner, StringComparison.Ordinal);
        Assert.Contains("Invoke-DownKyiTestProject", reviewRunner, StringComparison.Ordinal);
        Assert.Contains("delegated-cgroup-scope.ps1", ciActionRunner, StringComparison.Ordinal);
        Assert.Contains("delegated-cgroup-scope.ps1", lifecycleRunner, StringComparison.Ordinal);
        Assert.Contains("systemd-run", delegatedScope, StringComparison.Ordinal);
        Assert.Contains("Delegate=yes", delegatedScope, StringComparison.Ordinal);
    }

    [Fact]
    public void MacSigningBehaviorIsOwnedByMacOSProject()
    {
        var macProject = Path.Combine(
            RepositoryRoot,
            "tests",
            "DownKyi.MacOS.Tests",
            "DownKyi.MacOS.Tests.csproj");
        var windowsProject = Path.Combine(
            RepositoryRoot,
            "tests",
            "DownKyi.Windows.Tests",
            "DownKyi.Windows.Tests.csproj");
        var releaseArchitecture = Read(
            "tests/DownKyi.Architecture.Tests/ReleaseWorkflowArchitectureTests.cs");
        var macBehavior = Read("tests/DownKyi.MacOS.Tests/MacSigningScriptTests.cs");
        var buildWorkflow = Read(".github/workflows/build.yml");

        Assert.Equal(MacOnlyPlatforms, ReadDeclaredPlatforms(macProject));
        Assert.Equal(WindowsOnlyPlatforms, ReadDeclaredPlatforms(windowsProject));
        Assert.DoesNotContain("RunMacSigningFixture", releaseArchitecture, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "MacAdHocSigningExecutesUnderNounsetWithoutTimestamp",
            releaseArchitecture,
            StringComparison.Ordinal);
        Assert.Contains("FileName = \"/bin/bash\"", macBehavior, StringComparison.Ordinal);
        Assert.Contains("AdHocSigningExecutesUnderSystemBashNounsetWithoutTimestamp", macBehavior, StringComparison.Ordinal);
        Assert.Contains("DeveloperIdSigningIncludesTimestamp", macBehavior, StringComparison.Ordinal);
        Assert.Contains("macos-15", buildWorkflow, StringComparison.Ordinal);
    }

    [Fact]
    public void ProcessSupervisionBehaviorHasOneNativeProjectPerPlatform()
    {
        var windowsProject = Path.Combine(
            RepositoryRoot,
            "tests",
            "DownKyi.Windows.Tests",
            "DownKyi.Windows.Tests.csproj");
        var linuxProject = Path.Combine(
            RepositoryRoot,
            "tests",
            "DownKyi.Linux.Tests",
            "DownKyi.Linux.Tests.csproj");
        var macProject = Path.Combine(
            RepositoryRoot,
            "tests",
            "DownKyi.MacOS.Tests",
            "DownKyi.MacOS.Tests.csproj");

        Assert.Equal(WindowsOnlyPlatforms, ReadDeclaredPlatforms(windowsProject));
        Assert.Equal(LinuxOnlyPlatforms, ReadDeclaredPlatforms(linuxProject));
        Assert.Equal(MacOnlyPlatforms, ReadDeclaredPlatforms(macProject));
        Assert.All(
            new[] { windowsProject, linuxProject, macProject },
            project => Assert.Contains(
                "ProcessSupervisionTestCases/OwnedProcessLeasePlatformTests.cs",
                File.ReadAllText(project).Replace('\\', '/'),
                StringComparison.Ordinal));
    }

    [Fact]
    public void CrossPlatformProjectsCannotSilentlySkipTestsByOperatingSystem()
    {
        var violations = FindTestProjects()
            .Where(project =>
            {
                var platforms = ReadDeclaredPlatforms(project);
                return platforms.Length == AllowedPlatforms.Length &&
                       AllowedPlatforms.All(platforms.Contains);
            })
            .SelectMany(project => Directory.EnumerateFiles(
                Path.GetDirectoryName(project)!,
                "*.cs",
                SearchOption.AllDirectories))
            .Where(path => OperatingSystemSkipPattern().IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(RepositoryRoot, path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            violations.Length == 0,
            $"Cross-platform projects contain OS skip-and-return tests: {string.Join(", ", violations)}");
    }

    private static string[] FindTestProjects()
    {
        return Directory.EnumerateFiles(
                Path.Combine(RepositoryRoot, "tests"),
                "*.Tests.csproj",
                SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] ReadDeclaredPlatforms(string projectPath)
    {
        var document = XDocument.Load(projectPath);
        var declarations = document
            .Descendants()
            .Where(element => element.Name.LocalName == "DownKyiTestPlatforms")
            .ToArray();
        var relativePath = Path.GetRelativePath(RepositoryRoot, projectPath);

        Assert.True(
            declarations.Length == 1,
            $"{relativePath} must declare exactly one DownKyiTestPlatforms value.");

        var declaration = declarations[0];
        Assert.False(
            declaration.Attributes().Any(attribute => attribute.Name.LocalName == "Condition") ||
            declaration.Parent?.Attributes().Any(attribute => attribute.Name.LocalName == "Condition") == true,
            $"{relativePath} must declare DownKyiTestPlatforms unconditionally.");

        var tokens = declaration.Value.Split(';');
        Assert.All(
            tokens,
            token => Assert.False(
                string.IsNullOrWhiteSpace(token),
                $"{relativePath} contains an empty platform."));

        var platforms = tokens.Select(token => token.Trim()).ToArray();
        Assert.Equal(
            platforms.Length,
            platforms.Distinct(StringComparer.Ordinal).Count());
        return platforms;
    }

    private static string Read(string relativePath)
    {
        return File.ReadAllText(Path.Combine(
            RepositoryRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static bool IsBuildOutput(string path)
    {
        return path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex(
        @"if\s*\(\s*!\s*OperatingSystem\.Is(?:Windows|Linux|MacOS)\(\)\s*\)\s*\{\s*return\s*;",
        RegexOptions.CultureInvariant)]
    private static partial Regex OperatingSystemSkipPattern();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "DownKyi.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new DirectoryNotFoundException("Could not locate the DownKyi repository root.");
    }
}
