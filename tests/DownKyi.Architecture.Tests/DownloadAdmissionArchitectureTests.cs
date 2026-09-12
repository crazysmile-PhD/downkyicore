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
            < helper.IndexOf("selectDirectoryAsync()", StringComparison.Ordinal));
        Assert.Contains("addToDownloadSession.EnsureAdmissionAsync", content, StringComparison.Ordinal);
        Assert.Contains("addService.EnsureAdmissionAsync", video, StringComparison.Ordinal);
        Assert.Contains("_admissionPresenter.EnsureAdmissionAsync", session, StringComparison.Ordinal);
        Assert.True(File.ReadLines(Path.Combine(
            RepositoryRoot,
            "src", "DownKyi.Desktop", "Services", "Download", "AddToDownloadService.cs")).Count() <= 350);
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
