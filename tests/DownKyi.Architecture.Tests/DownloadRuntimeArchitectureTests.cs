namespace DownKyi.Architecture.Tests;

public sealed class DownloadRuntimeArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void DownloadRuntimeDoesNotUseSynchronousAsyncWaits()
    {
        var files = Directory.EnumerateFiles(
            Path.Combine(RepositoryRoot, "DownKyi", "Services", "Download"),
            "*.cs",
            SearchOption.TopDirectoryOnly).Append(
            Path.Combine(RepositoryRoot, "DownKyi.Core", "Aria2cNet", "AriaManager.cs"));
        var forbidden = new[]
        {
            ".GetAwaiter().GetResult()",
            ".Wait()",
            "Task.Run(async"
        };

        var violations = files
            .SelectMany(path => forbidden
                .Where(token => File.ReadAllText(path).Contains(token, StringComparison.Ordinal))
                .Select(token => $"{Path.GetRelativePath(RepositoryRoot, path)} -> {token}"))
            .ToArray();

        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void CustomAriaUsesTheSharedAriaTransferBackend()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "Services",
            "Download",
            "CustomAriaDownloadService.cs"));

        Assert.Contains(": AriaDownloadService", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TransferAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AriaManager", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyRuntimeUsesBoundedWorkersAndHasNoSynchronousPersistenceBridge()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "Services",
            "Download",
            "DownloadService.cs"));

        Assert.Contains("Channel.CreateBounded<DownloadingItem>", source, StringComparison.Ordinal);
        Assert.Contains("DownloadWorkerAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("void PersistDownloadingState(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadRuntimeUsesInjectedListAndStorageOwners()
    {
        var directory = Path.Combine(RepositoryRoot, "DownKyi", "Services", "Download");
        var violations = Directory.EnumerateFiles(directory, "*.cs", SearchOption.TopDirectoryOnly)
            .Select(path => new
            {
                Path = path,
                Source = File.ReadAllText(path)
            })
            .Where(file => file.Source.Contains("App.Current.Container.Resolve", StringComparison.Ordinal)
                || file.Source.Contains("App.DownloadingList", StringComparison.Ordinal)
                || file.Source.Contains("App.DownloadedList", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(RepositoryRoot, file.Path))
            .ToArray();

        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void DownloadBootstrapUsesExplicitRuntimeAndUiBoundaries()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "Services",
            "Download",
            "DownloadBootstrapHostedService.cs"));

        Assert.Contains("IDownloadRuntimeFactory", source, StringComparison.Ordinal);
        Assert.Contains("IUiDispatcher", source, StringComparison.Ordinal);
        Assert.Contains("Task.WhenAll(stopTasks)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Dispatcher.UIThread", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Container.Resolve", source, StringComparison.Ordinal);
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
