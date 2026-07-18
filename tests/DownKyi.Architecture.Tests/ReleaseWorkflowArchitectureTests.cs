namespace DownKyi.Architecture.Tests;

public sealed class ReleaseWorkflowArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void ReleaseWorkflowKeepsStrictCrossPlatformGateAndManualPackageValidation()
    {
        var workflow = File.ReadAllText(Path.Combine(RepositoryRoot, ".github", "workflows", "build.yml"));

        Assert.Contains("workflow_dispatch:", workflow, StringComparison.Ordinal);
        Assert.Contains("release-gate:", workflow, StringComparison.Ordinal);
        Assert.Contains("windows-latest", workflow, StringComparison.Ordinal);
        Assert.Contains("ubuntu-latest", workflow, StringComparison.Ordinal);
        Assert.Contains("macos-15", workflow, StringComparison.Ordinal);
        Assert.Contains("-p:AnalysisMode=All", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet test ./DownKyi.sln", workflow, StringComparison.Ordinal);
        Assert.Equal(3, CountOccurrences(workflow, "validate-publish-output.ps1"));
        Assert.Equal(3, CountOccurrences(workflow, "Get-FileHash"));
    }

    [Fact]
    public void PublishValidatorRequiresBothMediaToolsAndThePackagedDownloader()
    {
        var validator = File.ReadAllText(
            Path.Combine(RepositoryRoot, "script", "validate-publish-output.ps1"));

        Assert.Contains("ffmpeg/ffmpeg", validator, StringComparison.Ordinal);
        Assert.Contains("ffmpeg/ffprobe", validator, StringComparison.Ordinal);
        Assert.Contains("aria2/aria2c", validator, StringComparison.Ordinal);
        Assert.Contains("Avalonia.Themes.Fluent", validator, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash", validator, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string source, string value)
    {
        return source.Split(value, StringSplitOptions.None).Length - 1;
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
