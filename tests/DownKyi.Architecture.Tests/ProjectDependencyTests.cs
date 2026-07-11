using System.Xml.Linq;

namespace DownKyi.Architecture.Tests;

public sealed class ProjectDependencyTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void ProductionProjectReferencesAreAcyclic()
    {
        var graph = LoadProductionProjectGraph();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in graph.Keys)
        {
            Visit(project, graph, visited, active, new Stack<string>());
        }
    }

    [Fact]
    public void TargetArchitectureProjectsRespectDependencyDirection()
    {
        var graph = LoadProductionProjectGraph();
        var allowedDependencies = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["DownKyi.Domain"] = new(StringComparer.OrdinalIgnoreCase),
            ["DownKyi.Application"] = new(StringComparer.OrdinalIgnoreCase) { "DownKyi.Domain" },
            ["DownKyi.Infrastructure"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "DownKyi.Application",
                "DownKyi.Domain"
            },
            ["DownKyi.Desktop"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "DownKyi.Application",
                "DownKyi.Domain",
                "DownKyi.Infrastructure"
            }
        };

        foreach (var project in graph)
        {
            var projectName = Path.GetFileNameWithoutExtension(project.Key);
            if (!allowedDependencies.TryGetValue(projectName, out var allowed))
            {
                continue;
            }

            var invalid = project.Value
                .Select(referencePath => Path.GetFileNameWithoutExtension(referencePath)!)
                .Where(reference => reference.StartsWith("DownKyi.", StringComparison.OrdinalIgnoreCase))
                .Where(reference => !allowed.Contains(reference))
                .ToArray();

            Assert.True(
                invalid.Length == 0,
                $"{projectName} has forbidden project references: {string.Join(", ", invalid)}");
        }
    }

    [Fact]
    public void DomainProjectDoesNotReferenceFrameworkOrInfrastructurePackages()
    {
        var domainProject = Directory
            .EnumerateFiles(RepositoryRoot, "DownKyi.Domain.csproj", SearchOption.AllDirectories)
            .SingleOrDefault();
        if (domainProject == null)
        {
            return;
        }

        var forbiddenPrefixes = new[]
        {
            "Avalonia",
            "Prism",
            "Microsoft.Data.Sqlite",
            "Newtonsoft.Json",
            "FFMpegCore"
        };
        var packages = XDocument.Load(domainProject)
            .Descendants("PackageReference")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(package => package != null)
            .Cast<string>()
            .ToArray();
        var invalid = packages
            .Where(package => forbiddenPrefixes.Any(prefix =>
                package.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        Assert.True(
            invalid.Length == 0,
            $"Domain references forbidden packages: {string.Join(", ", invalid)}");
    }

    private static Dictionary<string, string[]> LoadProductionProjectGraph()
    {
        var projects = Directory
            .EnumerateFiles(RepositoryRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !IsUnderDirectory(path, "tests"))
            .Where(path => !IsUnderDirectory(path, "benchmarks"))
            .ToArray();
        var projectSet = projects.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return projects.ToDictionary(
            Path.GetFullPath,
            project => XDocument.Load(project)
                .Descendants("ProjectReference")
                .Select(element => (string?)element.Attribute("Include"))
                .Where(include => !string.IsNullOrWhiteSpace(include))
                .Select(include => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, include!)))
                .Where(projectSet.Contains)
                .ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static void Visit(
        string project,
        IReadOnlyDictionary<string, string[]> graph,
        ISet<string> visited,
        ISet<string> active,
        Stack<string> path)
    {
        if (visited.Contains(project))
        {
            return;
        }

        if (!active.Add(project))
        {
            var cycle = path.Reverse().Append(project).Select(Path.GetFileNameWithoutExtension);
            Assert.Fail($"Circular project dependency detected: {string.Join(" -> ", cycle)}");
        }

        path.Push(project);
        foreach (var dependency in graph[project])
        {
            Visit(dependency, graph, visited, active, path);
        }

        path.Pop();
        active.Remove(project);
        visited.Add(project);
    }

    private static bool IsUnderDirectory(string path, string directoryName)
    {
        var marker = Path.DirectorySeparatorChar + directoryName + Path.DirectorySeparatorChar;
        return path.Contains(marker, StringComparison.OrdinalIgnoreCase);
    }

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
