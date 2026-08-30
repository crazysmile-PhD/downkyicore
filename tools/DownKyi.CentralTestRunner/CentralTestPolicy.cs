using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml.Linq;

#pragma warning disable CA1515 // PowerShell compatibility wrappers invoke this compiled boundary.

namespace DownKyi.CentralTestRunner;

public sealed record CentralTestProjectPolicy(
    string Project,
    string Runner,
    string TargetFramework,
    string Parallel,
    string Reason);

public static class CentralTestPolicy
{
    private static readonly string[] AllowedPlatforms = ["Windows", "Linux", "macOS"];

    public static string GetCurrentPlatform()
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

        throw new PlatformNotSupportedException(
            "The current operating system has no declared DownKyi test platform.");
    }

    public static IReadOnlyList<string> ReadProjectPlatforms(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        var canonicalProject = Path.GetFullPath(projectPath);
        var diagnosticProject = Path.GetFileName(canonicalProject);
        var project = XDocument.Load(canonicalProject);
        var declarations = project
            .Descendants()
            .Where(element => element.Name.LocalName == "DownKyiTestPlatforms" &&
                              !element
                                  .AncestorsAndSelf()
                                  .Any(ancestor => ancestor.Attribute("Condition") != null))
            .ToArray();
        if (declarations.Length != 1)
        {
            throw new InvalidOperationException(
                $"Test project must declare exactly one unconditional DownKyiTestPlatforms value: {diagnosticProject}");
        }

        var tokens = declarations[0].Value.Split(';');
        if (tokens.Length == 0 || tokens.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException(
                $"DownKyiTestPlatforms contains an empty platform in {diagnosticProject}.");
        }

        var platforms = tokens.Select(token => token.Trim()).ToArray();
        foreach (var platform in platforms)
        {
            if (!AllowedPlatforms.Contains(platform, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Unsupported DownKyiTestPlatforms value '{platform}' in {diagnosticProject}. Allowed values: {string.Join(", ", AllowedPlatforms)}.");
            }
        }
        if (platforms.Distinct(StringComparer.Ordinal).Count() != platforms.Length)
        {
            throw new InvalidOperationException(
                $"Duplicate DownKyiTestPlatforms value in {diagnosticProject}.");
        }

        return new ReadOnlyCollection<string>(platforms);
    }

    public static bool SupportsPlatform(string projectPath, string platform)
    {
        ValidatePlatform(platform);
        return ReadProjectPlatforms(projectPath).Contains(platform, StringComparer.Ordinal);
    }

    public static IReadOnlyList<string> SelectProjects(
        IEnumerable<string> projectPaths,
        string platform)
    {
        ArgumentNullException.ThrowIfNull(projectPaths);
        ValidatePlatform(platform);
        return new ReadOnlyCollection<string>(
            projectPaths
                .Select(Path.GetFullPath)
                .Where(path => SupportsPlatform(path, platform))
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    public static CentralTestProjectPolicy ReadRunnerPolicy(
        string repositoryRoot,
        string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        var root = Path.GetFullPath(repositoryRoot);
        var policyPath = Path.Combine(root, "docs", "testing", "test-runner-policy.json");
        if (!File.Exists(policyPath))
        {
            throw new FileNotFoundException(
                "Test runner policy is missing.",
                "docs/testing/test-runner-policy.json");
        }

        using var document = JsonDocument.Parse(File.ReadAllText(policyPath));
        if (document.RootElement.GetProperty("schemaVersion").GetInt32() != 1)
        {
            throw new InvalidOperationException("Unsupported test runner policy schema.");
        }

        var relativeProject = Path.GetRelativePath(root, Path.GetFullPath(projectPath, root))
            .Replace('\\', '/');
        var matches = document.RootElement.GetProperty("projects")
            .EnumerateArray()
            .Where(entry => string.Equals(
                entry.GetProperty("project").GetString(),
                relativeProject,
                StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidOperationException(
                matches.Length == 0
                    ? $"Test runner policy has no entry for {relativeProject}."
                    : $"Test runner policy contains duplicate entries for {relativeProject}.");
        }

        var entry = matches[0];
        var policy = new CentralTestProjectPolicy(
            relativeProject,
            entry.GetProperty("runner").GetString() ?? string.Empty,
            entry.GetProperty("targetFramework").GetString() ?? string.Empty,
            entry.GetProperty("parallel").GetString() ?? string.Empty,
            entry.GetProperty("reason").GetString() ?? string.Empty);
        if (!string.Equals(policy.Runner, "xunit-in-process", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(policy.TargetFramework) ||
            !string.Equals(policy.Parallel, "none", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(policy.Reason))
        {
            throw new InvalidOperationException(
                $"Test runner policy is incomplete for {relativeProject}.");
        }

        return policy;
    }

    internal static IReadOnlyList<string> GetOwnedAssemblyPaths(string repositoryRoot)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var policyPath = Path.Combine(root, "docs", "testing", "test-runner-policy.json");
        using var document = JsonDocument.Parse(File.ReadAllText(policyPath));
        var assemblies = new List<string>();
        foreach (var entry in document.RootElement.GetProperty("projects").EnumerateArray())
        {
            var project = Path.GetFullPath(entry.GetProperty("project").GetString()!, root);
            var directory = Path.GetDirectoryName(project)
                ?? throw new InvalidOperationException("The test project directory is unavailable.");
            var assemblyName = Path.GetFileNameWithoutExtension(project);
            var framework = entry.GetProperty("targetFramework").GetString()
                ?? throw new InvalidOperationException("The test target framework is unavailable.");
            foreach (var configuration in new[] { "Debug", "Release" })
            {
                assemblies.Add(Path.GetFullPath(
                    Path.Combine("bin", configuration, framework, $"{assemblyName}.dll"),
                    directory));
            }
        }

        return assemblies;
    }

    private static void ValidatePlatform(string platform)
    {
        if (!AllowedPlatforms.Contains(platform, StringComparer.Ordinal))
        {
            throw new ArgumentOutOfRangeException(
                nameof(platform),
                platform,
                "The test platform is unsupported.");
        }
    }
}
