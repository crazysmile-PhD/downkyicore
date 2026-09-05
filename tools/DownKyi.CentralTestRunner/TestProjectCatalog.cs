using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml.Linq;

namespace DownKyi.CentralTestRunner;

internal sealed record TestProjectDefinition(
    string Project,
    string[] Platforms,
    string? InProcessTargetFramework)
{
    public bool UseInProcessXunit => InProcessTargetFramework is not null;
}

internal sealed record TestRunnerPolicyDocument(
    int SchemaVersion,
    TestRunnerPolicyProject[] Projects);

internal sealed record TestRunnerPolicyProject(
    string Project,
    string Runner,
    string? TargetFramework);

internal static class TestProjectCatalog
{
    private static readonly JsonSerializerOptions PolicyJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal static string NormalizeProject(string repositoryRoot, string project)
    {
        var fullPath = Path.GetFullPath(project, repositoryRoot);
        return Path.GetRelativePath(repositoryRoot, fullPath).Replace('\\', '/');
    }

    internal static TestProjectDefinition[] DiscoverProjects(string repositoryRoot)
    {
        var testsDirectory = Path.Combine(repositoryRoot, "tests");
        if (!Directory.Exists(testsDirectory))
        {
            throw new DirectoryNotFoundException($"The repository test directory is missing: {testsDirectory}");
        }

        var policyPath = Path.Combine(repositoryRoot, "docs", "testing", "test-runner-policy.json");
        if (!File.Exists(policyPath))
        {
            throw new FileNotFoundException("The test runner policy is missing.", policyPath);
        }

        var policy = JsonSerializer.Deserialize<TestRunnerPolicyDocument>(
            File.ReadAllText(policyPath),
            PolicyJsonOptions) ?? throw new InvalidDataException("The test runner policy is empty.");
        if (policy.SchemaVersion != 1 || policy.Projects is null)
        {
            throw new InvalidDataException("The test runner policy schema is unsupported.");
        }

        var policies = new Dictionary<string, TestRunnerPolicyProject>(StringComparer.Ordinal);
        foreach (var entry in policy.Projects)
        {
            var project = NormalizeProject(repositoryRoot, entry.Project);
            if (!policies.TryAdd(project, entry))
            {
                throw new InvalidDataException($"The test runner policy contains duplicate project '{project}'.");
            }
        }

        var projects = Directory.EnumerateFiles(
                testsDirectory,
                "*.Tests.csproj",
                SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .Order(StringComparer.Ordinal)
            .Select(path =>
            {
                var project = NormalizeProject(repositoryRoot, path);
                var platforms = ReadDeclaredPlatforms(path, project);
                policies.TryGetValue(project, out var runnerPolicy);
                if (runnerPolicy is not null &&
                    !string.Equals(runnerPolicy.Runner, "xunit-in-process", StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Test project '{project}' has unsupported runner '{runnerPolicy.Runner}'.");
                }
                if (runnerPolicy is not null && string.IsNullOrWhiteSpace(runnerPolicy.TargetFramework))
                {
                    throw new InvalidDataException(
                        $"Test project '{project}' requires a target framework for xunit-in-process.");
                }

                policies.Remove(project);
                return new TestProjectDefinition(
                    project,
                    platforms,
                    runnerPolicy?.TargetFramework);
            })
            .ToArray();

        if (policies.Count > 0)
        {
            throw new InvalidDataException(
                $"The test runner policy references unknown project(s): {string.Join(", ", policies.Keys.Order(StringComparer.Ordinal))}");
        }

        return projects;
    }

    internal static string GetCurrentPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "Windows";
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return "Linux";
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "macOS";
        }

        throw new PlatformNotSupportedException("The current operating system has no allowlisted test platform.");
    }

    private static string[] ReadDeclaredPlatforms(string projectPath, string projectIdentity)
    {
        var document = XDocument.Load(projectPath);
        var declarations = document
            .Descendants()
            .Where(element => string.Equals(
                element.Name.LocalName,
                "DownKyiTestPlatforms",
                StringComparison.Ordinal))
            .ToArray();
        if (declarations.Length != 1)
        {
            throw new InvalidDataException(
                $"Test project '{projectIdentity}' must declare exactly one DownKyiTestPlatforms value.");
        }

        var declaration = declarations[0];
        if (declaration.AncestorsAndSelf().Any(element =>
                element.Attributes().Any(attribute => string.Equals(
                    attribute.Name.LocalName,
                    "Condition",
                    StringComparison.Ordinal))))
        {
            throw new InvalidDataException(
                $"Test project '{projectIdentity}' must declare DownKyiTestPlatforms unconditionally.");
        }

        var platforms = declaration.Value.Split(';')
            .Select(value => value.Trim())
            .ToArray();
        if (platforms.Length == 0 ||
            platforms.Any(string.IsNullOrWhiteSpace) ||
            platforms.Distinct(StringComparer.Ordinal).Count() != platforms.Length)
        {
            throw new InvalidDataException(
                $"Test project '{projectIdentity}' has invalid DownKyiTestPlatforms metadata.");
        }

        var unknownPlatforms = platforms
            .Where(platform => platform is not ("Windows" or "Linux" or "macOS"))
            .ToArray();
        if (unknownPlatforms.Length > 0)
        {
            throw new InvalidDataException(
                $"Test project '{projectIdentity}' declares unknown platform(s): {string.Join(", ", unknownPlatforms)}.");
        }

        return platforms;
    }

    private static bool IsBuildOutput(string path)
    {
        return path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase));
    }
}
