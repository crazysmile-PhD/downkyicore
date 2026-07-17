namespace DownKyi.Architecture.Tests;

public sealed class LoggingMigrationArchitectureTests
{
    private static readonly string[] ProductionRoots = ["DownKyi", "DownKyi.Core", "src"];
    private static readonly string[] LegacyLoggingFiles =
    [
        "DownKyi.Core/Logging/LogManager.cs",
        "DownKyi.Core/Logging/LogInfo.cs",
        "DownKyi.Core/Logging/LogLevel.cs",
        "DownKyi.Core/Utils/Debugging/Console.cs"
    ];
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void ProductionCodeCannotUseLegacyStaticOrTerminalLogging()
    {
        var violations = ProductionRoots
            .Select(root => Path.Combine(RepositoryRoot, root))
            .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(path => !ContainsBuildOutputSegment(path))
            .Where(path =>
            {
                var source = File.ReadAllText(path);
                return source.Contains("LogManager.", StringComparison.Ordinal) ||
                       source.Contains("Console.Print", StringComparison.Ordinal) ||
                       source.Contains("DownKyi.Core.Utils.Debugging.Console", StringComparison.Ordinal);
            })
            .Select(path => Path.GetRelativePath(RepositoryRoot, path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void LegacyStaticLoggingTypesStayDeleted()
    {
        var existingFiles = LegacyLoggingFiles
            .Where(path => File.Exists(Path.Combine(
                RepositoryRoot,
                path.Replace('/', Path.DirectorySeparatorChar))))
            .ToArray();

        Assert.Empty(existingFiles);
    }

    [Fact]
    public void AsyncCommandsRequireInjectedDiagnostics()
    {
        var commandSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "Commands",
            "AsyncDelegateCommand.cs"));

        Assert.Contains("ILogger logger", commandSource, StringComparison.Ordinal);
        Assert.Contains("_logger.LogErrorMessage", commandSource, StringComparison.Ordinal);
        Assert.DoesNotContain("LogManager.", commandSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Console.Print", commandSource, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("DownKyi.Core/Utils/HardDisk.cs")]
    [InlineData("DownKyi.Core/Utils/ObjectHelper.cs")]
    [InlineData("DownKyi/CustomAction/ScrollIntoViewBehavior.cs")]
    [InlineData("DownKyi/Services/VersionCheckerService.cs")]
    [InlineData("DownKyi/ViewModels/Dialogs/ViewDownloadSetterViewModel.cs")]
    [InlineData("DownKyi/ViewModels/MainWindowViewModel.cs")]
    [InlineData("DownKyi/ViewModels/Toolbox/ViewBiliHelperViewModel.cs")]
    public void MigratedRuntimeFilesCannotRestoreStaticOrTerminalLogging(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

        Assert.DoesNotContain("LogManager.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Console.Print", source, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateChecksUseInjectedDiagnosticsAndCancellation()
    {
        var mainWindowSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "ViewModels",
            "MainWindowViewModel.cs"));
        var versionCheckerSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "Services",
            "VersionCheckerService.cs"));

        Assert.Contains("ILogger<MainWindowViewModel>", mainWindowSource, StringComparison.Ordinal);
        Assert.Contains("_lifetimeCancellation.Token", mainWindowSource, StringComparison.Ordinal);
        Assert.Contains("CancellationToken cancellationToken", versionCheckerSource, StringComparison.Ordinal);
        Assert.Contains("GetStringAsync(new Uri", versionCheckerSource, StringComparison.Ordinal);
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

    private static bool ContainsBuildOutputSegment(string path)
    {
        var relativePath = Path.GetRelativePath(RepositoryRoot, path);
        var segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Contains("bin", StringComparer.OrdinalIgnoreCase) ||
               segments.Contains("obj", StringComparer.OrdinalIgnoreCase);
    }
}
