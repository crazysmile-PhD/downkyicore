using System.Text.RegularExpressions;

namespace DownKyi.Architecture.Tests;

public sealed class IpcNamingArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void NamedPipeServersConsumeSharedPhysicalIdentifiers()
    {
        var violations = new List<string>();
        var sourceRoots = new[]
        {
            "DownKyi",
            "DownKyi.Core",
            "src",
            "tests",
            "benchmarks",
            "tools"
        };
        foreach (var path in sourceRoots.SelectMany(sourceRoot =>
                     Directory.EnumerateFiles(
                         Path.Combine(RepositoryRoot, sourceRoot),
                         "*.cs",
                         SearchOption.AllDirectories)))
        {
            var normalizedPath = path.Replace('\\', '/');
            if (normalizedPath.Contains("/bin/", StringComparison.Ordinal) ||
                normalizedPath.Contains("/obj/", StringComparison.Ordinal) ||
                normalizedPath.EndsWith(
                    "/IpcNamingArchitectureTests.cs",
                    StringComparison.Ordinal))
            {
                continue;
            }

            var source = File.ReadAllText(path);
            foreach (Match construction in Regex.Matches(
                         source,
                         @"new\s+NamedPipeServerStream\s*\(",
                         RegexOptions.CultureInvariant))
            {
                var snippetLength = Math.Min(512, source.Length - construction.Index);
                var snippet = source.Substring(construction.Index, snippetLength);
                if (!snippet.Contains(".PhysicalIdentifier", StringComparison.Ordinal))
                {
                    var lineNumber = source[..construction.Index].Count(
                        character => character == '\n') + 1;
                    violations.Add(
                        $"{Path.GetRelativePath(RepositoryRoot, path)}:{lineNumber}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "NamedPipeServerStream physical names must come from IpcEndpointName: " +
            string.Join(", ", violations));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null &&
               !File.Exists(Path.Combine(directory.FullName, "DownKyi.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
