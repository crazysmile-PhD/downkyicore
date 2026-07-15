namespace DownKyi.Architecture.Tests;

public sealed class LoggingMigrationArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

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
}
