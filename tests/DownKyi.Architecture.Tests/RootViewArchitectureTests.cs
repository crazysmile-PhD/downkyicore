using System.Xml;
using System.Xml.Linq;

namespace DownKyi.Architecture.Tests;

public sealed class RootViewArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void ProductionViewsDoNotUsePrismCompositionAttachedProperties()
    {
        var viewRoot = Path.Combine(RepositoryRoot, "DownKyi");
        var violations = new List<string>();
        foreach (var path in Directory.EnumerateFiles(viewRoot, "*.axaml", SearchOption.AllDirectories)
                     .Where(path => !IsUnderDirectory(path, "obj"))
                     .Where(path => !IsUnderDirectory(path, "bin")))
        {
            var document = XDocument.Load(path, LoadOptions.SetLineInfo);
            var forbiddenAttributes = document.Root!
                .DescendantsAndSelf()
                .Attributes()
                .Where(attribute => attribute.Name.LocalName is
                    "ViewModelLocator.AutoWireViewModel" or "RegionManager.RegionName")
                .Select(attribute => $"{attribute.Name} at line {((IXmlLineInfo)attribute).LineNumber}")
                .ToArray();
            if (forbiddenAttributes.Length > 0)
            {
                violations.Add(
                    $"{Path.GetRelativePath(RepositoryRoot, path)}: {string.Join(", ", forbiddenAttributes)}");
            }
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void ProductionSourceDoesNotReferencePrismContainerLocator()
    {
        var violations = Directory
            .EnumerateFiles(RepositoryRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsUnderDirectory(path, "tests"))
            .Where(path => !IsUnderDirectory(path, "obj"))
            .Where(path => !IsUnderDirectory(path, "bin"))
            .Where(path => File.ReadAllText(path).Contains("ContainerLocator", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(RepositoryRoot, path))
            .ToArray();

        Assert.True(
            violations.Length == 0,
            $"Production source references Prism ContainerLocator: {string.Join(", ", violations)}");
    }

    [Fact]
    public void ProductCodeDoesNotReferencePrismNamespaces()
    {
        var violations = Directory
            .EnumerateFiles(Path.Combine(RepositoryRoot, "DownKyi"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsUnderDirectory(path, "obj"))
            .Where(path => !IsUnderDirectory(path, "bin"))
            .Where(path => File.ReadAllText(path).Contains("Prism", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(RepositoryRoot, path))
            .ToArray();

        Assert.True(
            violations.Length == 0,
            $"Product source references Prism: {string.Join(", ", violations)}");
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
