namespace DownKyi.Architecture.Tests;

public sealed class DownloadAdmissionArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void ApplicationOwnsLegacyGatePolicyAndQueuedPersistenceEnforcement()
    {
        var service = ReadSource(
            "src", "DownKyi.Application", "Downloads", "DownloadTaskApplicationService.cs");
        var errors = ReadSource(
            "src", "DownKyi.Application", "Downloads", "DownloadAdmissionErrors.cs");
        var addStart = service.IndexOf(
            "public async Task<OperationResult<DownloadTask>> AddAsync",
            StringComparison.Ordinal);
        var gateCheck = service.IndexOf(
            "CheckNewDownloadAdmissionAsync(cancellationToken)",
            addStart,
            StringComparison.Ordinal);
        var storeAdd = service.IndexOf("_store.AddAsync(task", addStart, StringComparison.Ordinal);

        Assert.True(addStart >= 0 && gateCheck > addStart && storeAdd > gateCheck);
        Assert.Contains("task.Phase == DownloadPhase.Queued", service, StringComparison.Ordinal);
        Assert.Contains("IsLegacyUpgradeAdmissionBlockedAsync", service, StringComparison.Ordinal);
        Assert.Contains("OperationErrorKind.Conflict", errors, StringComparison.Ordinal);
        Assert.DoesNotContain("DownKyi.Infrastructure", service, StringComparison.Ordinal);
        Assert.DoesNotContain("download_upgrade_admission_gate", service, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopOnlyPresentsAndDelegatesTheApplicationGateResult()
    {
        var desktopRoot = Path.Combine(RepositoryRoot, "src", "DownKyi.Desktop");
        var sources = Directory.EnumerateFiles(desktopRoot, "*.cs", SearchOption.AllDirectories)
            .Select(path => (Path: path, Source: File.ReadAllText(path)))
            .ToArray();
        var presenter = sources.Single(item =>
            item.Path.EndsWith("LegacyDownloadAdmissionPresenter.cs", StringComparison.Ordinal));

        Assert.Contains("_tasks.CheckNewDownloadAdmissionAsync", presenter.Source, StringComparison.Ordinal);
        Assert.Contains("_tasks", presenter.Source, StringComparison.Ordinal);
        Assert.Contains("ConfirmLegacyRemoteTasksStoppedAsync", presenter.Source, StringComparison.Ordinal);
        Assert.Contains("IAppDialogService", presenter.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("IDownloadTaskStore", presenter.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("DownKyi.Infrastructure", presenter.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("Aria2cNet", presenter.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("download_upgrade_admission_gate", presenter.Source, StringComparison.Ordinal);
        Assert.True(File.ReadLines(presenter.Path).Count() <= 100);

        Assert.Equal(1, sources.Count(item =>
            item.Source.Contains("CheckNewDownloadAdmissionAsync", StringComparison.Ordinal)));
        Assert.Equal(1, sources.Count(item =>
            item.Source.Contains("ConfirmLegacyRemoteTasksStoppedAsync", StringComparison.Ordinal)));
        Assert.DoesNotContain(sources, item =>
            item.Source.Contains("IsLegacyUpgradeAdmissionBlockedAsync", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryDesktopAddEntryUsesSharedPreflightBeforeDirectorySelection()
    {
        var helper = ReadSource(
            "src", "DownKyi.Application", "Downloads", "DownloadAddCoordinator.cs");
        var content = ReadSource(
            "src", "DownKyi.Desktop", "Services", "Media", "ContentDownloadCoordinator.cs");
        var video = ReadSource(
            "src", "DownKyi.Desktop", "Services", "Video", "VideoDetailDownloadCoordinator.cs");
        var session = ReadSource(
            "src", "DownKyi.Desktop", "Services", "Download", "AddToDownloadService.cs");

        Assert.True(
            helper.IndexOf("ensureAdmissionAsync()", StringComparison.Ordinal)
            < helper.IndexOf("selectDownloadAsync()", StringComparison.Ordinal));
        Assert.Contains("addToDownloadSession.EnsureAdmissionAsync", content, StringComparison.Ordinal);
        Assert.Contains("addService.EnsureAdmissionAsync", video, StringComparison.Ordinal);
        Assert.Contains("_admissionPresenter.EnsureAdmissionAsync", session, StringComparison.Ordinal);
        Assert.True(File.ReadLines(Path.Combine(
            RepositoryRoot,
            "src", "DownKyi.Desktop", "Services", "Download", "AddToDownloadService.cs")).Count() <= 350);
    }

    [Fact]
    public void AddFlowRequiresExplicitSelectionPreparationAndFinalization()
    {
        var contract = ReadSource(
            "src", "DownKyi.Desktop", "Services", "Download", "IAddToDownloadSession.cs");
        var service = ReadSource(
            "src", "DownKyi.Desktop", "Services", "Download", "AddToDownloadService.cs");
        var selection = ReadSource(
            "src", "DownKyi.Application", "Downloads", "DownloadAddSelection.cs");
        var prepared = ReadSource(
            "src", "DownKyi.Desktop", "Services", "Download", "PreparedDownload.cs");
        var resolver = ReadSource(
            "src", "DownKyi.Desktop", "Services", "Download", "DownloadContentConflictResolver.cs");
        var planner = ReadSource(
            "src", "DownKyi.Desktop", "Services", "Download", "DownloadActionPlanner.cs");
        var duplicatePolicy = ReadSource(
            "src", "DownKyi.Desktop", "Services", "Download", "DownloadDuplicatePolicy.cs");
        var actionCoverage = ReadSource(
            "src", "DownKyi.Desktop", "Services", "Download", "DownloadActionCoverage.cs");
        var content = ReadSource(
            "src", "DownKyi.Desktop", "Services", "Media", "ContentDownloadCoordinator.cs");
        var video = ReadSource(
            "src", "DownKyi.Desktop", "Services", "Video", "VideoDetailDownloadCoordinator.cs");

        Assert.Contains("Task<DownloadAddSelection?> SelectDownloadAsync", contract, StringComparison.Ordinal);
        Assert.Contains("Task<PreparedDownload> PrepareAsync", contract, StringComparison.Ordinal);
        Assert.Contains("FinalizedDownload finalizedDownload", contract, StringComparison.Ordinal);
        Assert.Contains("DownloadContentSelection RequestedContent", selection, StringComparison.Ordinal);
        Assert.Contains("DownloadMediaCapabilities AvailableMedia", prepared, StringComparison.Ordinal);
        Assert.Contains("DownloadMediaOutputModes SupportedModes", prepared, StringComparison.Ordinal);
        Assert.Contains("HasAnyMedia", prepared, StringComparison.Ordinal);
        Assert.Contains("Supports(DownloadContentSelection", prepared, StringComparison.Ordinal);
        Assert.Contains("TryGetCompatibleContent(", prepared, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadMediaCapabilities(bool Video, bool Audio)", prepared, StringComparison.Ordinal);
        Assert.Contains("DownloadContentSelection RequestedContent", prepared, StringComparison.Ordinal);
        Assert.Contains("DownloadContentSelection FinalizedContent", prepared, StringComparison.Ordinal);
        Assert.Contains("DownloadContentConflictChoices choices", resolver, StringComparison.Ordinal);
        Assert.Contains("DownloadContentConflictDialogContract.ShowAsync", resolver, StringComparison.Ordinal);
        Assert.Contains("DownloadPlanningStopReason", planner, StringComparison.Ordinal);
        Assert.Contains("_conflictResolver", planner, StringComparison.Ordinal);
        Assert.Contains(".ResolveAsync(", planner, StringComparison.Ordinal);
        Assert.Contains("_actionPlanner", content, StringComparison.Ordinal);
        Assert.Contains("_actionPlanner", video, StringComparison.Ordinal);
        Assert.Contains(
            "DownloadActionCoverage.RemoveCoveredActions",
            duplicatePolicy,
            StringComparison.Ordinal);
        Assert.DoesNotContain("MediaParametersMatch", duplicatePolicy, StringComparison.Ordinal);
        Assert.Contains("MediaParametersMatch", actionCoverage, StringComparison.Ordinal);
        Assert.DoesNotContain("HasPlayback", planner, StringComparison.Ordinal);
        Assert.DoesNotContain("_downloadContent", service, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadContentConflict", service, StringComparison.Ordinal);
        Assert.DoesNotContain("SetVideoInfoService", contract, StringComparison.Ordinal);
        Assert.DoesNotContain("GetVideo(", contract, StringComparison.Ordinal);
        Assert.DoesNotContain("ParseVideoAsync", contract, StringComparison.Ordinal);
    }

    [Fact]
    public void BlockedCopyExplainsUncertaintyRemoteRiskAndCancelBehavior()
    {
        var resources = ReadSource(
            "src", "DownKyi.Desktop", "Languages", "Default.axaml");
        var start = resources.IndexOf(
            "x:Key=\"LegacyDownloadAdmissionBlocked\"",
            StringComparison.Ordinal);
        var end = resources.IndexOf("</system:String>", start, StringComparison.Ordinal);
        var copy = resources[start..end];

        Assert.Contains("保存位置不安全或已经变化", copy, StringComparison.Ordinal);
        Assert.Contains("无法确认", copy, StringComparison.Ordinal);
        Assert.Contains("远端 aria2 可能仍在写入文件", copy, StringComparison.Ordinal);
        Assert.Contains("避免两个 aria2 同时写入", copy, StringComparison.Ordinal);
        Assert.Contains("停止这些旧任务", copy, StringComparison.Ordinal);
        Assert.Contains("取消或关闭不会解除阻止", copy, StringComparison.Ordinal);
        Assert.DoesNotContain("一定在远端", copy, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateOutputAndRecoveryUseTheSharedActionClaimsContract()
    {
        var contract = ReadSource(
            "src", "DownKyi.Domain", "Downloads", "DownloadContentSelection.cs");
        var coverage = ReadSource(
            "src", "DownKyi.Desktop", "Services", "Download", "DownloadActionCoverage.cs");
        var outputResolver = ReadSource(
            "src", "DownKyi.Desktop", "Services", "Download", "DownloadOutputPathResolver.cs");
        var reservations = ReadSource(
            "src", "DownKyi.Infrastructure", "Downloads", "SqliteDownloadStoreOutputReservations.cs");
        var recovery = ReadSource(
            "src", "DownKyi.Infrastructure", "Downloads", "DownloadStoreReservationKeyCompatibility.cs");

        Assert.Contains("record struct DownloadActionClaims", contract, StringComparison.Ordinal);
        Assert.Contains("SubtractFrom(DownloadContentSelection", contract, StringComparison.Ordinal);
        Assert.Contains("coveredClaims.SubtractFrom(requestedContent)", coverage, StringComparison.Ordinal);
        Assert.Contains("requestedContent.ActionClaims", outputResolver, StringComparison.Ordinal);
        Assert.Contains("existingContent.ActionClaims.Overlaps", reservations, StringComparison.Ordinal);
        Assert.Contains("db.need_download_content", recovery, StringComparison.Ordinal);
        Assert.Contains("claims.Overlaps(row.Claims)", recovery, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadOutputClaims", coverage, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadOutputClaims", reservations, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadOutputClaims", recovery, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] segments)
    {
        return File.ReadAllText(Path.Combine([RepositoryRoot, .. segments]));
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
