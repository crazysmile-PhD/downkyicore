namespace DownKyi.Architecture.Tests;

public sealed class DownloadRuntimeArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void DownloadRuntimeDoesNotUseSynchronousAsyncWaits()
    {
        var ariaClientDirectory = Path.Combine(
            RepositoryRoot,
            "DownKyi.Core",
            "Aria2cNet",
            "Client");
        var files = Directory.EnumerateFiles(
            Path.Combine(RepositoryRoot, "src", "DownKyi.Desktop", "Services", "Download"),
            "*.cs",
            SearchOption.TopDirectoryOnly).Append(
            Path.Combine(RepositoryRoot, "DownKyi.Core", "Aria2cNet", "AriaManager.cs")).Concat(
            Directory.EnumerateFiles(
                ariaClientDirectory,
                "AriaClient*.cs",
                SearchOption.TopDirectoryOnly));
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
            "src", "DownKyi.Desktop",
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
        var runtimeDirectory = Path.Combine(RepositoryRoot, "src", "DownKyi.Desktop", "Services", "Download");
        var factorySource = File.ReadAllText(Path.Combine(runtimeDirectory, "DownloadRuntimeFactory.cs"));
        var backendSource = File.ReadAllText(Path.Combine(runtimeDirectory, "Aria2TransferBackend.cs"));

        Assert.Contains("public sealed partial class AriaClient", clientSource, StringComparison.Ordinal);
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
    public void AriaRpcClientKeepsProtocolResponsibilitiesSeparated()
    {
        var directory = Path.Combine(
            RepositoryRoot,
            "DownKyi.Core",
            "Aria2cNet",
            "Client");
        string[] expectedFiles =
        [
            "AriaClient.cs",
            "AriaClient.Downloads.cs",
            "AriaClient.Lifecycle.cs",
            "AriaClient.Options.cs",
            "AriaClient.Status.cs",
            "AriaClient.System.cs"
        ];

        var actualFiles = Directory
            .EnumerateFiles(directory, "AriaClient*.cs", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expectedFiles.Order(StringComparer.Ordinal), actualFiles);
        Assert.All(
            actualFiles,
            fileName => Assert.True(
                File.ReadAllLines(Path.Combine(directory, fileName!)).Length <= 500,
                $"{fileName} exceeds the 500-line owner limit."));

        var coreSource = File.ReadAllText(Path.Combine(directory, "AriaClient.cs"));
        Assert.Contains("GetRpcResponseAsync", coreSource, StringComparison.Ordinal);
        Assert.Contains("RequestAsync", coreSource, StringComparison.Ordinal);
        Assert.DoesNotContain("AddUriAsync", coreSource, StringComparison.Ordinal);
    }

    [Fact]
    public void OrchestratorUsesBoundedWorkersAndHasNoSynchronousPersistenceBridge()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src", "DownKyi.Desktop",
            "Services",
            "Download",
            "DownloadOrchestrator.cs"));

        Assert.Contains("Channel.CreateBounded<DownloadTaskId>", source, StringComparison.Ordinal);
        Assert.Contains("Channel.CreateUnbounded<DownloadTaskId>", source, StringComparison.Ordinal);
        Assert.Contains("ForwardAdmissionsAsync", source, StringComparison.Ordinal);
        Assert.Contains("DownloadWorkerAsync", source, StringComparison.Ordinal);
        Assert.Contains("_executor.ExecuteAsync(taskId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_executor.ExecuteAsync(downloading", source, StringComparison.Ordinal);
        Assert.DoesNotContain("void PersistDownloadingState(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadingItem", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DispatchAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_queuedDownloads", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_downloadLists", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Delay", source, StringComparison.Ordinal);

        var recoverySource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src", "DownKyi.Desktop",
            "Services",
            "Download",
            "DownloadTaskShutdownRecovery.cs"));
        Assert.Contains("IDownloadTaskApplicationService", recoverySource, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadListState", recoverySource, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadingItem", recoverySource, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadArtifactsAndTaskStateHaveDedicatedOwners()
    {
        var directory = Path.Combine(RepositoryRoot, "src", "DownKyi.Desktop", "Services", "Download");
        var pipelineSource = File.ReadAllText(Path.Combine(directory, "DownloadPipeline.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var artifactSource = File.ReadAllText(Path.Combine(directory, "DownloadArtifactWriter.cs"));
        var artifactStageSource = File.ReadAllText(Path.Combine(
            directory,
            "DownloadArtifactsStage.cs"));
        var stateSource = File.ReadAllText(Path.Combine(directory, "DownloadTaskStateWriter.cs"));
        var factorySource = File.ReadAllText(Path.Combine(directory, "DownloadRuntimeFactory.cs"));

        Assert.Contains("IReadOnlyList<IDownloadPipelineStage>", pipelineSource, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadArtifactWriter", pipelineSource, StringComparison.Ordinal);
        Assert.DoesNotContain("VideoStreamApi.GetSubtitle", pipelineSource, StringComparison.Ordinal);
        Assert.DoesNotContain("BilibiliDanmakuConverter", pipelineSource, StringComparison.Ordinal);
        Assert.DoesNotContain("XmlWriter.Create", pipelineSource, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateDownloadingAsync", pipelineSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.Data.Sqlite", pipelineSource, StringComparison.Ordinal);

        Assert.Contains("_client.GetSubtitleAsync", artifactSource, StringComparison.Ordinal);
        Assert.Contains("new BilibiliDanmakuConverter()", artifactSource, StringComparison.Ordinal);
        Assert.Contains("XmlWriter.Create", artifactSource, StringComparison.Ordinal);
        Assert.Contains("DownloadArtifactWriter _artifactWriter", artifactStageSource, StringComparison.Ordinal);
        Assert.Contains("IDownloadTaskApplicationService _tasks", stateSource, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadingItem", stateSource, StringComparison.Ordinal);
        Assert.Contains("new DownloadArtifactWriter(", factorySource, StringComparison.Ordinal);
        Assert.Contains("new DownloadArtifactsStage(artifactWriter)", factorySource, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadRuntimeUsesInjectedListAndStorageOwners()
    {
        var directory = Path.Combine(RepositoryRoot, "src", "DownKyi.Desktop", "Services", "Download");
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
        var directory = Path.Combine(RepositoryRoot, "src", "DownKyi.Desktop", "Services", "Download");
        var projectionSource = File.ReadAllText(Path.Combine(
            directory,
            "DownloadTaskProjectionStore.cs"));
        var reverseWrites = Directory.EnumerateFiles(directory, "*.cs", SearchOption.TopDirectoryOnly)
            .Where(path =>
            {
                var source = File.ReadAllText(path);
                return source.Contains(".Downloading.DownloadStatus =", StringComparison.Ordinal)
                       || source.Contains(".Downloading.Gid =", StringComparison.Ordinal)
                       || source.Contains(".Downloading.DownloadFiles.", StringComparison.Ordinal)
                       || source.Contains(".Downloading.DownloadedFiles.", StringComparison.Ordinal);
            })
            .Select(path => Path.GetRelativePath(RepositoryRoot, path))
            .ToArray();
        var compositionSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src", "DownKyi.Desktop",
            "Composition",
            "DesktopComposition.cs"));

        Assert.False(File.Exists(Path.Combine(directory, "DownloadStorageService.cs")));
        Assert.Contains("IDownloadTaskApplicationService _tasks", projectionSource, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadTask.Restore", projectionSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SqliteConnection", projectionSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.Data.Sqlite", projectionSource, StringComparison.Ordinal);
        Assert.True(reverseWrites.Length == 0, string.Join(Environment.NewLine, reverseWrites));
        Assert.Contains(
            "AddSingleton<DownloadTaskProjectionStore>()",
            compositionSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadRuntimeUsesInjectedSettingsAndDiagnosticOwners()
    {
        var directory = Path.Combine(RepositoryRoot, "src", "DownKyi.Desktop", "Services", "Download");
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
            "src", "DownKyi.Desktop",
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
        var directory = Path.Combine(RepositoryRoot, "src", "DownKyi.Desktop", "Services", "Download");
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
            "src", "DownKyi.Desktop",
            "ViewModels",
            "DownloadManager",
            "ViewDownloadingViewModel.cs"));
        var finishedViewModelSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src", "DownKyi.Desktop",
            "ViewModels",
            "DownloadManager",
            "ViewDownloadFinishedViewModel.cs"));
        var itemSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src", "DownKyi.Desktop",
            "ViewModels",
            "DownloadManager",
            "DownloadingItem.cs"));
        var viewSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src", "DownKyi.Desktop",
            "Views",
            "DownloadManager",
            "ViewDownloading.axaml"));
        var coordinatorSource = File.ReadAllText(Path.Combine(
            directory,
            "DownloadManagerCoordinator.cs"));
        var compositionSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src", "DownKyi.Desktop",
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
        Assert.DoesNotContain("Downloading.DownloadStatus =", taskFileSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Downloading.Gid =", taskFileSource, StringComparison.Ordinal);
        Assert.Contains("_stateWriter.CancelAsync", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("_stateWriter.DeleteAsync", coordinatorSource, StringComparison.Ordinal);
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
            "src", "DownKyi.Desktop",
            "Composition",
            "DesktopComposition.cs"));
        var lifecycleSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src", "DownKyi.Desktop",
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
            "src", "DownKyi.Desktop",
            "Services",
            "Download",
            "DownloadBootstrapHostedService.cs"));

        Assert.Contains("IDownloadRuntimeFactory", source, StringComparison.Ordinal);
        Assert.Contains("QueueStartupTasksAsync", source, StringComparison.Ordinal);
        Assert.Contains("runtime.EnqueueAsync(taskId", source, StringComparison.Ordinal);
        Assert.Contains("IReadOnlyList<DownloadTask> tasks", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "new DownloadTaskId(item.DownloadBase.Id)",
            source,
            StringComparison.Ordinal);
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
            "src", "DownKyi.Desktop",
            "Services",
            "Download",
            "DownloadCompletionProjector.cs"));

        Assert.Contains("IUiDispatcher", source, StringComparison.Ordinal);
        Assert.Contains("return _uiDispatcher.InvokeAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("App.PropertyChange", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Dispatcher.UIThread", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeApisUseTaskIdentityAndTransferCallbacksInsteadOfUiItems()
    {
        var directory = Path.Combine(RepositoryRoot, "src", "DownKyi.Desktop", "Services", "Download");
        var pipelineSource = File.ReadAllText(Path.Combine(directory, "DownloadPipeline.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var transferSource = File.ReadAllText(Path.Combine(directory, "ITransferBackend.cs"));

        Assert.Contains("public async Task ExecuteAsync(\n        DownloadTaskId taskId", pipelineSource, StringComparison.Ordinal);
        Assert.Contains("public Task MarkFailedAsync(", pipelineSource, StringComparison.Ordinal);
        Assert.DoesNotContain("internal async Task ExecuteAsync(\n        DownloadingItem", pipelineSource, StringComparison.Ordinal);
        Assert.DoesNotContain("CancellationToken? CancellationToken", pipelineSource, StringComparison.Ordinal);
        Assert.DoesNotContain("CancellationToken.GetValueOrDefault", pipelineSource, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadingList.Any", pipelineSource, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadingItem Download", transferSource, StringComparison.Ordinal);
        Assert.Contains("DownloadTaskId TaskId", transferSource, StringComparison.Ordinal);
        Assert.Contains("Func<DownloadProgress, CancellationToken, Task> PersistProgressAsync", transferSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PauseIsAcknowledgedOnlyAfterTheTransferWorkerStops()
    {
        var directory = Path.Combine(RepositoryRoot, "src", "DownKyi.Desktop", "Services", "Download");
        var stateSource = File.ReadAllText(Path.Combine(directory, "DownloadTaskStateWriter.cs"));
        var orchestratorSource = File.ReadAllText(Path.Combine(directory, "DownloadOrchestrator.cs"));
        var mediaStageSource = File.ReadAllText(Path.Combine(directory, "DownloadMediaStage.cs"));
        var builtinSource = File.ReadAllText(Path.Combine(directory, "BuiltinTransferBackend.cs"));
        var ariaSource = File.ReadAllText(Path.Combine(directory, "Aria2TransferBackend.cs"));

        Assert.DoesNotContain("paused.Phase == DownloadPhase.Pausing", stateSource, StringComparison.Ordinal);
        Assert.Contains("ConfirmPauseAfterWorkerStopsAsync", orchestratorSource, StringComparison.Ordinal);
        Assert.Contains("_stateWriter.ConfirmPausedAsync", orchestratorSource, StringComparison.Ordinal);
        Assert.Contains("result.Outcome == DownloadTransferOutcome.Paused", mediaStageSource, StringComparison.Ordinal);
        Assert.Contains("return DownloadTransferResult.Paused()", builtinSource, StringComparison.Ordinal);
        Assert.Contains("return DownloadTransferResult.Paused()", ariaSource, StringComparison.Ordinal);
        Assert.True(
            builtinSource.IndexOf("if (request.IsPauseRequested())", StringComparison.Ordinal) <
            builtinSource.IndexOf("request.EnsureActive();", StringComparison.Ordinal));
        Assert.True(
            ariaSource.IndexOf("if (request.IsPauseRequested())", StringComparison.Ordinal) <
                   ariaSource.IndexOf("request.EnsureActive();", StringComparison.Ordinal));
    }

    [Fact]
    public void TransferRetryHasOneTypedBudgetOwner()
    {
        var directory = Path.Combine(RepositoryRoot, "src", "DownKyi.Desktop", "Services", "Download");
        var coordinatorSource = File.ReadAllText(Path.Combine(
            directory,
            "DownloadTransferCoordinator.cs"));
        var policySource = File.ReadAllText(Path.Combine(
            directory,
            "DownloadRetryPolicy.cs"));
        var mediaStageSource = File.ReadAllText(Path.Combine(
            directory,
            "DownloadMediaStage.cs"));
        var builtinSource = File.ReadAllText(Path.Combine(
            directory,
            "BuiltinTransferBackend.cs"));
        var ariaSource = File.ReadAllText(Path.Combine(
            directory,
            "Aria2TransferBackend.cs"));

        Assert.Contains(
            "attempt <= _retryPolicy.MaximumAttempts",
            coordinatorSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "DownloadTransferFailureKind",
            policySource,
            StringComparison.Ordinal);
        Assert.Contains(
            "RefreshAddresses",
            policySource,
            StringComparison.Ordinal);
        Assert.DoesNotContain("RetryLimit", mediaStageSource, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadWithRetryAsync", mediaStageSource, StringComparison.Ordinal);
        Assert.Contains(
            "DownloadTransferFileCleanup.DeleteInvalidArtifacts",
            coordinatorSource,
            StringComparison.Ordinal);
        Assert.Contains("if (!cleanup.Succeeded)", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("request.Urls.Count != 1", builtinSource, StringComparison.Ordinal);
        Assert.Contains("request.Urls.Count != 1", ariaSource, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var url in", builtinSource, StringComparison.Ordinal);
        Assert.Contains("MaxTryAgainOnFailure = 0", builtinSource, StringComparison.Ordinal);
        Assert.Contains("MaxTries = \"1\"", ariaSource, StringComparison.Ordinal);
        Assert.Contains("RetryWait = \"0\"", ariaSource, StringComparison.Ordinal);
        Assert.Contains("AlwaysResume = \"false\"", ariaSource, StringComparison.Ordinal);
        Assert.Contains("MaxResumeFailureTries = \"0\"", ariaSource, StringComparison.Ordinal);
        Assert.Contains("GetDownloadStatusDetailAsync", ariaSource, StringComparison.Ordinal);
        var ariaClientSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi.Core",
            "Aria2cNet",
            "Client",
            "AriaClient.cs"));
        Assert.DoesNotContain(
            "int retry =",
            ariaClientSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "attempt < retry",
            ariaClientSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadPipelineOnlyOrdersTypedStages()
    {
        var directory = Path.Combine(RepositoryRoot, "src", "DownKyi.Desktop", "Services", "Download");
        var pipelineSource = File.ReadAllText(Path.Combine(directory, "DownloadPipeline.cs"));
        var factorySource = File.ReadAllText(Path.Combine(directory, "DownloadRuntimeFactory.cs"));
        var mediaSource = File.ReadAllText(Path.Combine(directory, "DownloadMediaStage.cs"));
        var muxSource = File.ReadAllText(Path.Combine(directory, "MuxStage.cs"));
        var transferKeySource = File.ReadAllText(Path.Combine(directory, "DownloadTransferKey.cs"));
        string[] stageNames =
        [
            "ResolvePlaybackStage",
            "DownloadMediaStage",
            "DownloadArtifactsStage",
            "MuxStage",
            "ValidateStage",
            "FinalizeStage"
        ];

        Assert.Contains("foreach (var stage in stages)", pipelineSource, StringComparison.Ordinal);
        Assert.Contains("if (!result.IsSuccess)", pipelineSource, StringComparison.Ordinal);
        Assert.Contains(
            "return new DownloadStageRunResult(result, stage.Name);",
            pipelineSource,
            StringComparison.Ordinal);
        Assert.Contains("OperationResult<DownloadStageResult>", File.ReadAllText(Path.Combine(
            directory,
            "IDownloadPipelineStage.cs")), StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadingItem", pipelineSource, StringComparison.Ordinal);
        Assert.DoesNotContain("DictionaryResource", pipelineSource, StringComparison.Ordinal);
        Assert.DoesNotContain("FfmpegProcessor", pipelineSource, StringComparison.Ordinal);
        Assert.True(File.ReadAllLines(Path.Combine(directory, "DownloadPipeline.cs")).Length < 150);
        Assert.False(File.Exists(Path.Combine(
            RepositoryRoot,
            "src", "DownKyi.Desktop",
            "Models",
            "VideoPlayUrlBasic.cs")));
        Assert.Contains("DownloadTransferKey.Create", mediaSource, StringComparison.Ordinal);
        Assert.DoesNotContain("GetHashCode", mediaSource, StringComparison.Ordinal);
        Assert.DoesNotContain("GetHashCode", transferKeySource, StringComparison.Ordinal);
        Assert.Equal(
            3,
            System.Text.RegularExpressions.Regex.Count(
                muxSource,
                "overwriteDestination: false",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant));
        Assert.Contains("InvalidInputPaths", muxSource, StringComparison.Ordinal);
        Assert.Contains(
            "DownloadTransferFileCleanup.DeleteInvalidArtifacts",
            muxSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "_stateWriter.InvalidateCompletedFilesAsync",
            muxSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "FfmpegOperationFailureKind.InvalidInput",
            muxSource,
            StringComparison.Ordinal);
        Assert.Contains("if (cleanedKeys.Count > 0)", muxSource, StringComparison.Ordinal);
        Assert.Contains("return cleanupFailed", muxSource, StringComparison.Ordinal);

        var previousIndex = -1;
        foreach (var stageName in stageNames)
        {
            var stagePath = Path.Combine(directory, $"{stageName}.cs");
            Assert.True(File.Exists(stagePath), $"Missing pipeline stage: {stageName}");
            var stageSource = File.ReadAllText(stagePath);
            Assert.DoesNotContain("DictionaryResource", stageSource, StringComparison.Ordinal);
            Assert.DoesNotContain("DownloadListState", stageSource, StringComparison.Ordinal);
            Assert.DoesNotContain("ImmutableObservableCollection", stageSource, StringComparison.Ordinal);
            Assert.DoesNotContain("DownKyi.ViewModels", stageSource, StringComparison.Ordinal);
            Assert.DoesNotContain("Microsoft.Data.Sqlite", stageSource, StringComparison.Ordinal);

            var currentIndex = factorySource.IndexOf(
                $"new {stageName}",
                StringComparison.Ordinal);
            Assert.True(currentIndex > previousIndex, $"{stageName} is out of order.");
            previousIndex = currentIndex;
        }
    }

    [Fact]
    public void TransferInputCleanupOccursOnlyAfterDurableCompletion()
    {
        var downloadDirectory = Path.Combine(
            RepositoryRoot,
            "src", "DownKyi.Desktop", "Services", "Download");
        var processorSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi.Core", "FFmpeg", "FfmpegProcessor.cs"));
        var concatSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi.Core", "FFmpeg", "FfmpegConcatRuntime.cs"));
        var finalizeSource = File.ReadAllText(Path.Combine(
            downloadDirectory,
            "FinalizeStage.cs"));

        Assert.DoesNotContain("DeleteInput", processorSource, StringComparison.Ordinal);
        Assert.DoesNotContain("DeleteSourceSegments", concatSource, StringComparison.Ordinal);
        Assert.True(
            finalizeSource.IndexOf("_stateWriter.CompleteAsync", StringComparison.Ordinal) <
            finalizeSource.IndexOf("DeleteTransferFilesAsync", StringComparison.Ordinal));
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
