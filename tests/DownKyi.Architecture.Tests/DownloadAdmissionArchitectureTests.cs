namespace DownKyi.Architecture.Tests;

public sealed class DownloadAdmissionArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void RetiredGlobalPreflightCannotReturnToTheDownloadAddFlow()
    {
        string[] retiredTokens =
        [
            "CheckNewDownloadAdmissionAsync",
            "DownloadAdmissionOutcome",
            "EnsureAdmissionAsync",
            "DownloadAddFlowOutcome.AdmissionCanceled",
            "DownloadAddFlowOutcome.AdmissionFailed",
            "DownloadAddStopReason.AdmissionCanceled",
            "DownloadAddStopReason.AdmissionFailed",
            "TipAddDownloadingAdmissionFailed"
        ];
        var sourceRoot = Path.Combine(RepositoryRoot, "src");
        var violations = Directory
            .EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Where(path => string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetExtension(path), ".axaml", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .Select(path => new { Path = path, Source = File.ReadAllText(path) })
            .SelectMany(file => retiredTokens
                .Where(token => file.Source.Contains(token, StringComparison.Ordinal))
                .Select(token => $"{Path.GetRelativePath(RepositoryRoot, file.Path)}: {token}"))
            .ToArray();
        var reservationContract = ReadSource(
            "src", "DownKyi.Application", "Downloads", "IDownloadOutputReservationService.cs");
        var reservationService = ReadSource(
            "src", "DownKyi.Application", "Downloads", "DownloadTaskApplicationService.OutputReservations.cs");
        var storeContract = ReadSource(
            "src", "DownKyi.Application", "Downloads", "IDownloadTaskStore.cs");
        var admissionService = ReadSource(
            "src", "DownKyi.Desktop", "Services", "Download", "Planning", "DownloadTaskAdmissionService.cs");
        var reservations = ReadSource(
            "src", "DownKyi.Infrastructure", "Downloads", "SqliteDownloadStoreOutputReservations.cs");
        var presenterPath = Path.Combine(
            RepositoryRoot,
            "src", "DownKyi.Desktop", "Services", "Download", "LegacyDownloadAdmissionPresenter.cs");

        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
        Assert.False(File.Exists(presenterPath));
        Assert.DoesNotContain("IsOutputPathReservedAsync", reservationContract, StringComparison.Ordinal);
        Assert.DoesNotContain("IsOutputPathReservedAsync", reservationService, StringComparison.Ordinal);
        Assert.Contains("IsOutputPathReservedAsync", storeContract, StringComparison.Ordinal);

        Assert.Contains("public async Task<bool> AdmitAsync(", admissionService, StringComparison.Ordinal);
        Assert.Contains("_admissionGate.WaitAsync", admissionService, StringComparison.Ordinal);
        Assert.Contains("_runtimeAvailability.EnsureAcceptingTasks();", admissionService, StringComparison.Ordinal);
        Assert.Contains("IDownloadTaskLookup", admissionService, StringComparison.Ordinal);
        Assert.Contains("IDownloadHistoryService", admissionService, StringComparison.Ordinal);
        Assert.Contains("GetUnfinishedByCidAsync", admissionService, StringComparison.Ordinal);
        Assert.Contains("GetByCidAsync", admissionService, StringComparison.Ordinal);
        Assert.Contains("DuplicateKey.From", admissionService, StringComparison.Ordinal);
        Assert.Contains("_projections.AddDownloadingAsync", admissionService, StringComparison.Ordinal);
        Assert.Contains("_database.ExecuteImmediateTransactionAsync", reservations, StringComparison.Ordinal);
        Assert.Contains("HasOutputClaimConflictCoreAsync", reservations, StringComparison.Ordinal);
    }

    [Fact]
    public void AddFlowRequiresExplicitSelectionPreparationAndFinalization()
    {
        var contract = ReadSource(
            "src", "DownKyi.Desktop", "Services", "Download", "Planning", "IAddToDownloadSession.cs");
        var service = ReadSource(
            "src", "DownKyi.Desktop", "Services", "Download", "Planning", "AddToDownloadService.cs");
        var selection = ReadSource(
            "src", "DownKyi.Application", "Downloads", "DownloadAddSelection.cs");
        var prepared = ReadSource(
            "src", "DownKyi.Desktop", "Services", "Download", "Planning", "PreparedDownload.cs");
        var resolver = ReadSource(
            "src", "DownKyi.Desktop", "Services", "Download", "Planning", "DownloadContentConflictResolver.cs");
        var planner = ReadSource(
            "src", "DownKyi.Desktop", "Services", "Download", "Planning", "DownloadActionPlanner.cs");
        var content = ReadSource(
            "src", "DownKyi.Desktop", "Services", "Media", "ContentDownloadCoordinator.cs");
        var video = ReadSource(
            "src", "DownKyi.Desktop", "Services", "Video", "VideoDetailDownloadCoordinator.cs");

        Assert.Contains("Task<DownloadSelectionResult> SelectDownloadAsync", contract, StringComparison.Ordinal);
        Assert.Contains("Task<PreparedDownload> PrepareAsync", contract, StringComparison.Ordinal);
        Assert.Contains("IReadOnlyList<FinalizedDownload> finalizedDownloads", contract, StringComparison.Ordinal);
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
        Assert.Contains("ResolveQualitySubstitutionBatchAsync", resolver, StringComparison.Ordinal);
        Assert.Contains("DownloadPlanningStopReason", planner, StringComparison.Ordinal);
        Assert.Contains("PlanBatchAsync", planner, StringComparison.Ordinal);
        Assert.Contains("CollectQualitySubstitutionItems", planner, StringComparison.Ordinal);
        Assert.Contains("_conflictResolver", planner, StringComparison.Ordinal);
        Assert.Contains(".ResolveAsync(", planner, StringComparison.Ordinal);
        Assert.Contains("_actionPlanner", content, StringComparison.Ordinal);
        Assert.Contains(".PlanBatchAsync(", content, StringComparison.Ordinal);
        Assert.Contains("_actionPlanner", video, StringComparison.Ordinal);
        Assert.DoesNotContain("HasPlayback", planner, StringComparison.Ordinal);
        Assert.DoesNotContain("_downloadContent", service, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadContentConflict", service, StringComparison.Ordinal);
        Assert.DoesNotContain("SetVideoInfoService", contract, StringComparison.Ordinal);
        Assert.DoesNotContain("GetVideo(", contract, StringComparison.Ordinal);
        Assert.DoesNotContain("ParseVideoAsync", contract, StringComparison.Ordinal);
    }

    [Fact]
    public void OutputAdmissionUsesTheSharedActionClaimsContract()
    {
        var contract = ReadSource(
            "src", "DownKyi.Domain", "Downloads", "DownloadContentSelection.cs");
        var outputResolver = ReadSource(
            "src", "DownKyi.Desktop", "Services", "Download", "Artifacts", "DownloadOutputPathResolver.cs");
        var outputLayout = ReadSource(
            "src", "DownKyi.Desktop", "Services", "Download", "Artifacts", "DownloadOutputLayout.cs");
        var reservations = ReadSource(
            "src", "DownKyi.Infrastructure", "Downloads", "SqliteDownloadStoreOutputReservations.cs");
        var store = ReadSource(
            "src", "DownKyi.Application", "Downloads", "IDownloadTaskStore.cs");

        Assert.Contains("record struct DownloadActionClaims", contract, StringComparison.Ordinal);
        Assert.Contains("record struct DownloadOutputOwnership", contract, StringComparison.Ordinal);
        Assert.Contains("ConflictsWith(DownloadOutputOwnership other)", contract, StringComparison.Ordinal);
        Assert.Contains("Cid != other.Cid || Claims.Overlaps(other.Claims)", contract, StringComparison.Ordinal);
        Assert.Contains("Nfo = 32", contract, StringComparison.Ordinal);
        Assert.Contains("DownloadOutputLayout.HasOutputClaimConflict", outputResolver, StringComparison.Ordinal);
        Assert.Contains("DownloadActionClaim.Nfo", outputLayout, StringComparison.Ordinal);
        Assert.Contains("ConflictsWith(requestedOwnership)", store, StringComparison.Ordinal);
        Assert.Contains("ConflictsWith(requestedOwnership)", reservations, StringComparison.Ordinal);
        Assert.DoesNotContain("task.Metadata.Media.Cid != requestedCid", store, StringComparison.Ordinal);
        Assert.DoesNotContain("reader.GetInt64(2) != requestedCid", reservations, StringComparison.Ordinal);
        Assert.DoesNotContain("existingClaims.Overlaps(requestedClaims)", reservations, StringComparison.Ordinal);
        Assert.Contains("db.nfo_request IS NOT NULL", reservations, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadOutputClaims", store, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadOutputClaims", reservations, StringComparison.Ordinal);
    }

    [Fact]
    public void QuarantineExclusionSqlHasOneOwner()
    {
        string[] exclusionTokens =
        [
            "q.source_table = 'downloading'",
            "q.source_table = 'download_history'"
        ];
        var ownerPath = Path.Combine(
            RepositoryRoot,
            "src", "DownKyi.Infrastructure", "Downloads", "SqliteDownloadStoreQuarantine.cs");
        var violations = Directory
            .EnumerateFiles(Path.Combine(RepositoryRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .Where(path => !string.Equals(path, ownerPath, StringComparison.OrdinalIgnoreCase))
            .Select(path => new { Path = path, Source = File.ReadAllText(path) })
            .SelectMany(file => exclusionTokens
                .Where(token => file.Source.Contains(token, StringComparison.Ordinal))
                .Select(token => $"{Path.GetRelativePath(RepositoryRoot, file.Path)}: {token}"))
            .ToArray();
        var owner = File.ReadAllText(ownerPath);
        var queries = ReadSource(
            "src", "DownKyi.Infrastructure", "Downloads", "SqliteDownloadStoreQueries.cs");
        var reservations = ReadSource(
            "src", "DownKyi.Infrastructure", "Downloads", "SqliteDownloadStoreOutputReservations.cs");
        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
        Assert.Contains("ExcludeDownloadingRecordSql", owner, StringComparison.Ordinal);
        Assert.Contains("ExcludeDownloadHistoryRecordSql", owner, StringComparison.Ordinal);
        Assert.All(exclusionTokens, token => Assert.Contains(token, owner, StringComparison.Ordinal));
        Assert.Contains(
            "SqliteDownloadStoreQuarantine.ExcludeDownloadingRecordSql",
            queries,
            StringComparison.Ordinal);
        Assert.Contains(
            "SqliteDownloadStoreQuarantine.ExcludeDownloadHistoryRecordSql",
            queries,
            StringComparison.Ordinal);
        Assert.Contains(
            "SqliteDownloadStoreQuarantine.ExcludeDownloadingRecordSql",
            reservations,
            StringComparison.Ordinal);
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
