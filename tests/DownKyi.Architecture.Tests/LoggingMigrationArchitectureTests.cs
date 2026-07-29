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
    public void LoggingContractsAndImplementationStayInTheirDeclaredProjects()
    {
        Assert.True(File.Exists(Path.Combine(
            RepositoryRoot,
            "src",
            "DownKyi.Application",
            "Diagnostics",
            "IApplicationLogService.cs")));
        Assert.True(File.Exists(Path.Combine(
            RepositoryRoot,
            "src",
            "DownKyi.Infrastructure",
            "Logging",
            "ApplicationLogProvider.cs")));
        Assert.True(File.Exists(Path.Combine(
            RepositoryRoot,
            "src",
            "DownKyi.Infrastructure",
            "Logging",
            "NLogAsyncRollingFileSink.cs")));

        var coreLoggingRoot = Path.Combine(RepositoryRoot, "DownKyi.Core", "Logging");
        var coreImplementations = Directory.Exists(coreLoggingRoot)
            ? Directory.EnumerateFiles(coreLoggingRoot, "*.cs", SearchOption.AllDirectories).ToArray()
            : [];

        Assert.Empty(coreImplementations);
    }

    [Fact]
    public void NLogRemainsAnInfrastructurePrivateSink()
    {
        var allowedRoot = Path.GetFullPath(Path.Combine(
            RepositoryRoot,
            "src",
            "DownKyi.Infrastructure",
            "Logging")) + Path.DirectorySeparatorChar;
        var violations = ProductionRoots
            .Select(root => Path.Combine(RepositoryRoot, root))
            .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(path => !ContainsBuildOutputSegment(path))
            .Where(path => File.ReadAllText(path).Contains("NLog", StringComparison.Ordinal))
            .Where(path => !Path.GetFullPath(path).StartsWith(
                allowedRoot,
                StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(RepositoryRoot, path))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var sinkSource = File.ReadAllText(Path.Combine(
            allowedRoot,
            "NLogAsyncRollingFileSink.cs"));
        var packageManifest = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "Directory.Packages.props"));

        Assert.Empty(violations);
        Assert.Contains("new LogFactory", sinkSource, StringComparison.Ordinal);
        Assert.DoesNotContain("LogManager.", sinkSource, StringComparison.Ordinal);
        Assert.DoesNotContain("NLog.Extensions.Logging", packageManifest, StringComparison.Ordinal);
    }

    [Fact]
    public void AsyncCommandsRequireInjectedDiagnostics()
    {
        var commandSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src", "DownKyi.Desktop",
            "Commands",
            "DownKyiAsyncDelegateCommand.cs"));

        Assert.Contains("ILogger logger", commandSource, StringComparison.Ordinal);
        Assert.Contains("_logger.LogErrorMessage", commandSource, StringComparison.Ordinal);
        Assert.DoesNotContain("LogManager.", commandSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Console.Print", commandSource, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("DownKyi.Core/Utils/HardDisk.cs")]
    [InlineData("DownKyi.Core/Utils/ObjectHelper.cs")]
    [InlineData("src/DownKyi.Desktop/CustomAction/ScrollIntoViewBehavior.cs")]
    [InlineData("src/DownKyi.Desktop/Services/VersionCheckerService.cs")]
    [InlineData("src/DownKyi.Desktop/ViewModels/Dialogs/ViewDownloadSetterViewModel.cs")]
    [InlineData("src/DownKyi.Desktop/ViewModels/MainWindowViewModel.cs")]
    [InlineData("src/DownKyi.Desktop/ViewModels/Toolbox/ViewBiliHelperViewModel.cs")]
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
            "src", "DownKyi.Desktop",
            "ViewModels",
            "MainWindowViewModel.cs"));
        var versionCheckerSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src", "DownKyi.Desktop",
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
