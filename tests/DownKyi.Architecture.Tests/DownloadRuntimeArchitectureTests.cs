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
            Path.Combine(RepositoryRoot, "DownKyi.Core", "Aria2cNet", "AriaManager.cs")).Append(
            Path.Combine(RepositoryRoot, "DownKyi.Core", "Aria2cNet", "Client", "AriaClient.cs"));
        var forbidden = new[]
        {
            ".GetAwaiter().GetResult()",
            ".Wait()",
            "Task.Run(async",
            "HttpClient.Send(",
            "Request(url, parameters, retry - 1)"
        };

        var violations = files
            .SelectMany(path => forbidden
                .Where(token => File.ReadAllText(path).Contains(token, StringComparison.Ordinal))
                .Select(token => $"{Path.GetRelativePath(RepositoryRoot, path)} -> {token}"))
            .ToArray();

        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void LocalAndCustomAriaUseOneTransferBackend()
    {
        var directory = Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "Services",
            "Download");
        var factorySource = File.ReadAllText(Path.Combine(directory, "DownloadRuntimeFactory.cs"));

        Assert.False(File.Exists(Path.Combine(directory, "CustomAriaDownloadService.cs")));
        Assert.Equal(
            2,
            factorySource.Split("new Aria2TransferBackend(", StringSplitOptions.None).Length - 1);
        Assert.Contains("ownsAriaServer: true", factorySource, StringComparison.Ordinal);
        Assert.Contains("ownsAriaServer: false", factorySource, StringComparison.Ordinal);
    }

    [Fact]
    public void AriaRpcConfigurationIsOwnedByEachDownloadRuntime()
    {
        var clientSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi.Core",
            "Aria2cNet",
            "Client",
            "AriaClient.cs"));
        var runtimeDirectory = Path.Combine(RepositoryRoot, "DownKyi", "Services", "Download");
        var factorySource = File.ReadAllText(Path.Combine(runtimeDirectory, "DownloadRuntimeFactory.cs"));
        var backendSource = File.ReadAllText(Path.Combine(runtimeDirectory, "Aria2TransferBackend.cs"));

        Assert.Contains("public sealed class AriaClient", clientSource, StringComparison.Ordinal);
        Assert.DoesNotContain("public static class AriaClient", clientSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SetToken(", clientSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SetHost(", clientSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SetListenPort(", clientSource, StringComparison.Ordinal);
        Assert.Equal(
            2,
            factorySource.Split("new AriaClient(", StringSplitOptions.None).Length - 1);
        Assert.Contains("private readonly AriaClient _ariaClient", backendSource, StringComparison.Ordinal);
        Assert.DoesNotContain("AriaClient.", backendSource, StringComparison.Ordinal);
    }

    [Fact]
    public void OrchestratorUsesBoundedWorkersAndHasNoSynchronousPersistenceBridge()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "Services",
            "Download",
            "DownloadOrchestrator.cs"));

        Assert.Contains("Channel.CreateBounded<DownloadingItem>", source, StringComparison.Ordinal);
        Assert.Contains("DownloadWorkerAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("void PersistDownloadingState(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadArtifactsAndTaskStateHaveDedicatedOwners()
    {
        var directory = Path.Combine(RepositoryRoot, "DownKyi", "Services", "Download");
        var pipelineSource = File.ReadAllText(Path.Combine(directory, "DownloadPipeline.cs"));
        var artifactSource = File.ReadAllText(Path.Combine(directory, "DownloadArtifactWriter.cs"));
        var stateSource = File.ReadAllText(Path.Combine(directory, "DownloadTaskStateWriter.cs"));
        var factorySource = File.ReadAllText(Path.Combine(directory, "DownloadRuntimeFactory.cs"));

        Assert.Contains("DownloadArtifactWriter ArtifactWriter", pipelineSource, StringComparison.Ordinal);
        Assert.Contains("DownloadTaskStateWriter StateWriter", pipelineSource, StringComparison.Ordinal);
        Assert.DoesNotContain("VideoStreamApi.GetSubtitle", pipelineSource, StringComparison.Ordinal);
        Assert.DoesNotContain("BilibiliDanmakuConverter", pipelineSource, StringComparison.Ordinal);
        Assert.DoesNotContain("XmlWriter.Create", pipelineSource, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateDownloadingAsync", pipelineSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.Data.Sqlite", pipelineSource, StringComparison.Ordinal);

        Assert.Contains("VideoStreamApi.GetSubtitle", artifactSource, StringComparison.Ordinal);
        Assert.Contains("new BilibiliDanmakuConverter()", artifactSource, StringComparison.Ordinal);
        Assert.Contains("XmlWriter.Create", artifactSource, StringComparison.Ordinal);
        Assert.Contains("UpdateDownloadingAsync", stateSource, StringComparison.Ordinal);
        Assert.Contains("new DownloadArtifactWriter(", factorySource, StringComparison.Ordinal);
        Assert.Contains("new DownloadTaskStateWriter(", factorySource, StringComparison.Ordinal);
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
    public void DesktopProjectionUsesTheApplicationStoreWithoutOwningSqlite()
    {
        var directory = Path.Combine(RepositoryRoot, "DownKyi", "Services", "Download");
        var projectionSource = File.ReadAllText(Path.Combine(
            directory,
            "DownloadTaskProjectionStore.cs"));
        var compositionSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "Composition",
            "DesktopComposition.cs"));

        Assert.False(File.Exists(Path.Combine(directory, "DownloadStorageService.cs")));
        Assert.Contains("IDownloadTaskStore _store", projectionSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SqliteConnection", projectionSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.Data.Sqlite", projectionSource, StringComparison.Ordinal);
        Assert.Contains(
            "AddSingleton<DownloadTaskProjectionStore>()",
            compositionSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadRuntimeUsesInjectedSettingsAndDiagnosticOwners()
    {
        var directory = Path.Combine(RepositoryRoot, "DownKyi", "Services", "Download");
        string[] runtimeOwners =
        [
            "DownloadRuntimeFactory.cs",
            "DownloadOrchestrator.cs",
            "DownloadPipeline.cs",
            "DownloadArtifactWriter.cs",
            "DownloadTaskStateWriter.cs",
            "BuiltinTransferBackend.cs",
            "Aria2TransferBackend.cs",
            "DownloadDiagnosticLogger.cs"
        ];
        var violations = runtimeOwners
            .Select(file => Path.Combine(directory, file))
            .Where(path => File.ReadAllText(path).Contains("SettingsManager.Instance", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(RepositoryRoot, path))
            .ToArray();
        var diagnosticSource = File.ReadAllText(Path.Combine(directory, "DownloadDiagnosticLogger.cs"));
        var compositionSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "Composition",
            "DesktopComposition.cs"));

        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
        Assert.Contains("sealed class DownloadDiagnosticLogger", diagnosticSource, StringComparison.Ordinal);
        Assert.DoesNotContain("static class DownloadDiagnosticLogger", diagnosticSource, StringComparison.Ordinal);
        Assert.Contains("AddSingleton<DownloadDiagnosticLogger>()", compositionSource, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadManagerUsesCoordinatorAndInjectedRuntimeBoundaries()
    {
        var directory = Path.Combine(RepositoryRoot, "DownKyi", "Services", "Download");
        var violations = Directory.EnumerateFiles(directory, "*.cs", SearchOption.TopDirectoryOnly)
            .Where(path =>
            {
                var source = File.ReadAllText(path);
                return source.Contains("LogManager.", StringComparison.Ordinal)
                       || source.Contains("Console.Print", StringComparison.Ordinal);
            })
            .Select(path => Path.GetRelativePath(RepositoryRoot, path))
            .ToArray();
        var taskFileSource = File.ReadAllText(Path.Combine(directory, "DownloadTaskFileService.cs"));
        var viewModelSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "ViewModels",
            "DownloadManager",
            "ViewDownloadingViewModel.cs"));
        var finishedViewModelSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "ViewModels",
            "DownloadManager",
            "ViewDownloadFinishedViewModel.cs"));
        var itemSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "ViewModels",
            "DownloadManager",
            "DownloadingItem.cs"));
        var viewSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "Views",
            "DownloadManager",
            "ViewDownloading.axaml"));
        var coordinatorSource = File.ReadAllText(Path.Combine(
            directory,
            "DownloadManagerCoordinator.cs"));
        var compositionSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "Composition",
            "DesktopComposition.cs"));

        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
        Assert.Contains("sealed class DownloadTaskFileService", taskFileSource, StringComparison.Ordinal);
        Assert.DoesNotContain("static class DownloadTaskFileService", taskFileSource, StringComparison.Ordinal);
        Assert.Contains("IDownloadManagerCoordinator _downloadManagerCoordinator", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("IDownloadManagerCoordinator _downloadManagerCoordinator", finishedViewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadStorageService", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadTaskFileService", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadStorageService", finishedViewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("File.", finishedViewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("StartOrPauseCommand", itemSource, StringComparison.Ordinal);
        Assert.Contains("ToggleDownloadingCommand", viewSource, StringComparison.Ordinal);
        Assert.Contains("DownloadFileDeletionResult", taskFileSource, StringComparison.Ordinal);
        Assert.Contains("RemoveDownloadingAsync", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("DeleteGeneratedFilesAsync", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("AddSingleton<DownloadTaskFileService>()", compositionSource, StringComparison.Ordinal);
        Assert.Contains(
            "AddSingleton<IDownloadManagerCoordinator, DownloadManagerCoordinator>()",
            compositionSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AriaRuntimeUsesOneInjectedServerAndTypedLogging()
    {
        var ariaDirectory = Path.Combine(RepositoryRoot, "DownKyi.Core", "Aria2cNet");
        var violations = Directory
            .EnumerateFiles(ariaDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("LogManager.", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(RepositoryRoot, path))
            .ToArray();
        var serverSource = File.ReadAllText(Path.Combine(ariaDirectory, "Server", "AriaServer.cs"));
        var compositionSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "Composition",
            "DesktopComposition.cs"));
        var lifecycleSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "Platform",
            "AvaloniaApplicationLifecycle.cs"));

        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
        Assert.Contains("sealed class AriaServer", serverSource, StringComparison.Ordinal);
        Assert.DoesNotContain("static class AriaServer", serverSource, StringComparison.Ordinal);
        Assert.Contains("ILoggerFactory loggerFactory", serverSource, StringComparison.Ordinal);
        Assert.Contains("AddSingleton<AriaServer>()", compositionSource, StringComparison.Ordinal);
        Assert.Contains("GetService<AriaServer>()", lifecycleSource, StringComparison.Ordinal);
        Assert.DoesNotContain("AriaServer.KillTrackedServer", lifecycleSource, StringComparison.Ordinal);
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

    [Fact]
    public void DownloadRuntimeProjectsCollectionsThroughInjectedUiDispatcher()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "Services",
            "Download",
            "DownloadPipeline.cs"));

        Assert.Contains("IUiDispatcher", source, StringComparison.Ordinal);
        Assert.Contains("await UiDispatcher.InvokeAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("App.PropertyChange", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Dispatcher.UIThread", source, StringComparison.Ordinal);
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
