namespace DownKyi.Architecture.Tests;

public sealed class DesktopPlatformBoundaryTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void ViewModelsUseTheClipboardApplicationBoundary()
    {
        var viewModelDirectory = Path.Combine(RepositoryRoot, "DownKyi", "ViewModels");
        var violations = Directory
            .EnumerateFiles(viewModelDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("ClipboardManager", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(RepositoryRoot, path))
            .ToArray();
        var appSource = File.ReadAllText(Path.Combine(RepositoryRoot, "DownKyi", "App.axaml.cs"));
        var compositionSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "Composition",
            "LegacyPrismComposition.cs"));

        Assert.Empty(violations);
        Assert.DoesNotContain("AvaloniaClipboardService", appSource, StringComparison.Ordinal);
        Assert.Contains("IClipboardService, AvaloniaClipboardService", compositionSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewModelsUseTheFilePickerApplicationBoundary()
    {
        var viewModelDirectory = Path.Combine(RepositoryRoot, "DownKyi", "ViewModels");
        var violations = Directory
            .EnumerateFiles(viewModelDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("DialogUtils", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(RepositoryRoot, path))
            .ToArray();
        var appSource = File.ReadAllText(Path.Combine(RepositoryRoot, "DownKyi", "App.axaml.cs"));
        var compositionSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "Composition",
            "LegacyPrismComposition.cs"));

        Assert.Empty(violations);
        Assert.DoesNotContain("AvaloniaFilePickerService", appSource, StringComparison.Ordinal);
        Assert.Contains("IFilePickerService, AvaloniaFilePickerService", compositionSource, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DownKyi.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
