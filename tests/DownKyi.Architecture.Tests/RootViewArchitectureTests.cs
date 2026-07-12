using System.Xml.Linq;

namespace DownKyi.Architecture.Tests;

public sealed class RootViewArchitectureTests
{
    private const string PrismNamespace = "http://prismlibrary.com/";
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string[] HostIndependentRootViews =
    [
        Path.Combine("DownKyi", "Views", "MainWindow.axaml")
    ];
    private static readonly HashSet<string> ApprovedCompatibilityBridges = new(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void HostIndependentRootViewsDoNotUsePrismCompositionAttachedProperties()
    {
        foreach (var relativePath in HostIndependentRootViews)
        {
            var document = XDocument.Load(Path.Combine(RepositoryRoot, relativePath), LoadOptions.SetLineInfo);
            var forbiddenAttributes = document.Root!
                .DescendantsAndSelf()
                .Attributes()
                .Where(attribute => attribute.Name.NamespaceName == PrismNamespace)
                .Where(attribute => attribute.Name.LocalName is
                    "ViewModelLocator.AutoWireViewModel" or "RegionManager.RegionName")
                .Select(attribute => attribute.Name.LocalName)
                .ToArray();

            Assert.True(
                forbiddenAttributes.Length == 0 || ApprovedCompatibilityBridges.Contains(relativePath),
                $"Host root '{relativePath}' uses Prism composition: {string.Join(", ", forbiddenAttributes)}");
        }
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
