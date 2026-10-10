using DownKyi.Application.Downloads;
using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
using DownKyi.Infrastructure.Downloads;
using DownKyi.Infrastructure.Time;
using DownKyi.Models;
using DownKyi.Services.Download;
using DownKyi.ViewModels.DownloadManager;
using Microsoft.Data.Sqlite;

namespace DownKyi.Tests;

public sealed class DownloadTaskAdmissionServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "downkyi-admission-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ConcurrentAdmissionsPersistDistinctOutputPathsBeforeQueueing()
    {
        Directory.CreateDirectory(_directory);
        using var store = CreateStore();
        var clock = new SystemClock();
        var historyService = DownloadHistoryService.CreateForSharedStore(store);
        using var tasks = new DownloadTaskApplicationService(store, historyService, clock);
        using var projections = new DownloadTaskProjectionStore(
            tasks,
            historyService,
            clock);
        var listState = new DownloadListState();
        var queue = new RecordingDownloadTaskQueue();
        using var admission = CreateAdmission(listState, tasks, projections, queue);
        var basePath = Path.Combine(_directory, "same-output");
        var first = CreateItem("first", basePath);
        var second = CreateItem("second", basePath);

        await Task.WhenAll(
            admission.AdmitAsync(first, true, TestContext.Current.CancellationToken),
            admission.AdmitAsync(second, true, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        var persisted = await tasks
            .GetUnfinishedAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        var persistedPaths = persisted.Select(task => task.Output.BasePath).ToArray();
        Assert.Equal(2, persistedPaths.Distinct(DownloadOutputPathResolver.PlatformComparer).Count());
        Assert.Contains(basePath, persistedPaths);
        Assert.Contains($"{basePath}(1)", persistedPaths);
        Assert.Equal(2, listState.Downloading.Count);
        Assert.Equal(2, queue.Enqueued.Count);
    }

    [Fact]
    public async Task DifferentCidDisjointActionGetsNumberedBasePathBeforeAnyOutputExists()
    {
        Directory.CreateDirectory(_directory);
        using var store = CreateStore();
        var clock = new SystemClock();
        var historyService = DownloadHistoryService.CreateForSharedStore(store);
        using var tasks = new DownloadTaskApplicationService(store, historyService, clock);
        using var projections = new DownloadTaskProjectionStore(tasks, historyService, clock);
        var list = new DownloadListState();
        var queue = new RecordingDownloadTaskQueue();
        using var admission = CreateAdmission(list, tasks, projections, queue);
        var basePath = Path.Combine(_directory, "cross-cid-auto");
        var media = CreateItem("cross-cid-media", basePath, cid: 1001);
        media.DownloadBase.NeedDownloadContent =
            DownloadContentSelection.None with { Video = true };
        await admission.AdmitAsync(media, true, TestContext.Current.CancellationToken);
        var subtitle = CreateItem("cross-cid-subtitle", basePath, cid: 2002);
        subtitle.DownloadBase.NeedDownloadContent = DownloadContentSelection.None with
        {
            Subtitle = true,
            SelectedSubtitleTrackIds = [11]
        };
        Assert.DoesNotContain(
            Directory.EnumerateFiles(_directory),
            path => Path.GetFileName(path).StartsWith(
                "cross-cid-auto",
                StringComparison.OrdinalIgnoreCase));

        await admission.AdmitAsync(
            subtitle,
            autoAddNumberSuffix: true,
            cancellationToken: TestContext.Current.CancellationToken,
            allowExistingBasePath: true);

        Assert.Equal(basePath, media.DownloadBase.FilePath);
        Assert.Equal(basePath + "(1)", subtitle.DownloadBase.FilePath);
        Assert.Equal(2, list.Downloading.Count);
        Assert.Equal(2, queue.Enqueued.Count);
        Assert.Equal(2, (await tasks.GetUnfinishedAsync(
            TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task DifferentCidDisjointActionWithoutAutoSuffixHasNoAdmissionSideEffects()
    {
        Directory.CreateDirectory(_directory);
        using var innerStore = CreateStore();
        var store = new CountingDownloadTaskStore(innerStore);
        var clock = new SystemClock();
        var historyService = DownloadHistoryService.CreateForSharedStore(store);
        using var tasks = new DownloadTaskApplicationService(store, historyService, clock);
        using var projections = new DownloadTaskProjectionStore(tasks, historyService, clock);
        var list = new DownloadListState();
        var queue = new RecordingDownloadTaskQueue();
        using var admission = CreateAdmission(list, tasks, projections, queue);
        var basePath = Path.Combine(_directory, "cross-cid-reject");
        var media = CreateItem("cross-cid-existing", basePath, cid: 1001);
        media.DownloadBase.NeedDownloadContent =
            DownloadContentSelection.None with { Video = true };
        await admission.AdmitAsync(media, true, TestContext.Current.CancellationToken);
        var subtitle = CreateItem("cross-cid-rejected", basePath, cid: 2002);
        subtitle.DownloadBase.NeedDownloadContent = DownloadContentSelection.None with
        {
            Subtitle = true,
            SelectedSubtitleTrackIds = [11]
        };

        await Assert.ThrowsAsync<IOException>(() => admission.AdmitAsync(
            subtitle,
            autoAddNumberSuffix: false,
            cancellationToken: TestContext.Current.CancellationToken,
            allowExistingBasePath: true));

        Assert.Equal(basePath, subtitle.DownloadBase.FilePath);
        Assert.Single(await tasks.GetUnfinishedAsync(TestContext.Current.CancellationToken));
        Assert.Single(list.Downloading);
        Assert.Single(queue.Enqueued);
        Assert.Equal(1, store.AddCallCount);
    }

    [Fact]
    public async Task SameCidOverlappingActionsKeepExistingCollisionBehavior()
    {
        Directory.CreateDirectory(_directory);
        using var store = CreateStore();
        var clock = new SystemClock();
        var historyService = DownloadHistoryService.CreateForSharedStore(store);
        using var tasks = new DownloadTaskApplicationService(store, historyService, clock);
        using var projections = new DownloadTaskProjectionStore(tasks, historyService, clock);
        using var admission = CreateAdmission(
            new DownloadListState(),
            tasks,
            projections,
            new RecordingDownloadTaskQueue());
        var basePath = Path.Combine(_directory, "same-cid-overlap");
        var first = CreateItem("same-cid-first", basePath, cid: 1001);
        first.DownloadBase.NeedDownloadContent =
            DownloadContentSelection.None with { Video = true };
        var second = CreateItem("same-cid-second", basePath, cid: 1001);
        second.DownloadBase.NeedDownloadContent =
            DownloadContentSelection.None with { Video = true };

        await admission.AdmitAsync(first, true, TestContext.Current.CancellationToken);
        await admission.AdmitAsync(second, true, TestContext.Current.CancellationToken);

        Assert.Equal(basePath + "(1)", second.DownloadBase.FilePath);
    }

    [Fact]
    public async Task FailedRetryableTaskRetainsItsOutputReservation()
    {
        Directory.CreateDirectory(_directory);
        using var store = CreateStore();
        var clock = new SystemClock();
        var historyService = DownloadHistoryService.CreateForSharedStore(store);
        using var tasks = new DownloadTaskApplicationService(store, historyService, clock);
        using var projections = new DownloadTaskProjectionStore(
            tasks,
            historyService,
            clock);
        using var admission = CreateAdmission(
            new DownloadListState(),
            tasks,
            projections,
            new RecordingDownloadTaskQueue());
        var basePath = Path.Combine(_directory, "retryable-output");
        var first = CreateItem("failed", basePath);
        await admission.AdmitAsync(first, true, TestContext.Current.CancellationToken).ConfigureAwait(true);
        var taskId = new DownloadTaskId(first.DownloadBase.Id);
        Assert.True((await tasks
            .StartAsync(taskId, TestContext.Current.CancellationToken)
            .ConfigureAwait(true)).IsSuccess);
        Assert.True((await tasks
            .FailAsync(
                taskId,
                new DownloadFailure("download.failed", "Transfer failed.", true),
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true)).IsSuccess);

        var second = CreateItem("second", basePath);
        await admission.AdmitAsync(second, true, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal($"{basePath}(1)", second.DownloadBase.FilePath);
    }

    [Fact]
    public async Task CanceledTaskRetainsItsOutputReservationUntilDeleted()
    {
        Directory.CreateDirectory(_directory);
        using var store = CreateStore();
        var clock = new SystemClock();
        var historyService = DownloadHistoryService.CreateForSharedStore(store);
        using var tasks = new DownloadTaskApplicationService(store, historyService, clock);
        using var projections = new DownloadTaskProjectionStore(
            tasks,
            historyService,
            clock);
        using var admission = CreateAdmission(
            new DownloadListState(),
            tasks,
            projections,
            new RecordingDownloadTaskQueue());
        var basePath = Path.Combine(_directory, "canceled-output");
        var first = CreateItem("canceled", basePath);
        await admission.AdmitAsync(first, true, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.True((await tasks
            .CancelAsync(new DownloadTaskId(first.DownloadBase.Id), TestContext.Current.CancellationToken)
            .ConfigureAwait(true)).IsSuccess);

        var second = CreateItem("replacement", basePath);
        await admission.AdmitAsync(second, true, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal($"{basePath}(1)", second.DownloadBase.FilePath);

        Assert.True((await tasks
            .DeleteAsync(new DownloadTaskId(first.DownloadBase.Id), TestContext.Current.CancellationToken)
            .ConfigureAwait(true)).IsSuccess);
        var third = CreateItem("after-cleanup", basePath);
        await admission.AdmitAsync(third, true, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(Path.GetFullPath(basePath), third.DownloadBase.FilePath);
    }

    [Fact]
    public async Task AdmissionSkipsDiskCollisionCreatedAfterDraftConstruction()
    {
        Directory.CreateDirectory(_directory);
        using var innerStore = CreateStore();
        var store = new CountingDownloadTaskStore(innerStore);
        var clock = new SystemClock();
        var historyService = DownloadHistoryService.CreateForSharedStore(store);
        using var tasks = new DownloadTaskApplicationService(store, historyService, clock);
        using var projections = new DownloadTaskProjectionStore(
            tasks,
            historyService,
            clock);
        using var admission = CreateAdmission(
            new DownloadListState(),
            tasks,
            projections,
            new RecordingDownloadTaskQueue());
        var basePath = Path.Combine(_directory, "late-disk-collision");
        var draft = CreateItem("draft", basePath);
        await File.WriteAllTextAsync(
            $"{basePath}.mp4",
            "foreign output",
            TestContext.Current.CancellationToken);

        await admission.AdmitAsync(draft, true, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal($"{basePath}(1)", draft.DownloadBase.FilePath);
        Assert.Equal(0, store.ReservationProbeCount);
        Assert.Equal(1, store.ReservationSnapshotCount);
    }

    [Fact]
    public async Task DisabledAutoSuffixRejectsCollisionWithoutRenamingOrPersisting()
    {
        Directory.CreateDirectory(_directory);
        using var innerStore = CreateStore();
        var store = new CountingDownloadTaskStore(innerStore);
        var clock = new SystemClock();
        var historyService = DownloadHistoryService.CreateForSharedStore(store);
        using var tasks = new DownloadTaskApplicationService(store, historyService, clock);
        using var projections = new DownloadTaskProjectionStore(
            tasks,
            historyService,
            clock);
        using var admission = CreateAdmission(
            new DownloadListState(),
            tasks,
            projections,
            new RecordingDownloadTaskQueue());
        var basePath = Path.Combine(_directory, "no-auto-suffix");
        await File.WriteAllTextAsync(
            $"{basePath}.mp4",
            "occupied",
            TestContext.Current.CancellationToken);
        var item = CreateItem("rejected", basePath);

        await Assert.ThrowsAsync<IOException>(() => admission.AdmitAsync(
            item,
            false,
            TestContext.Current.CancellationToken));

        Assert.Equal(basePath, item.DownloadBase.FilePath);
        Assert.Empty(await tasks.GetUnfinishedAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, store.ReservationProbeCount);
        Assert.Equal(0, store.ReservationSnapshotCount);
        Assert.Equal(0, store.AddCallCount);
    }

    [Fact]
    public async Task DisabledAutoSuffixRejectsDatabaseCollisionWithoutSnapshotOrSideEffects()
    {
        Directory.CreateDirectory(_directory);
        using var innerStore = CreateStore();
        var store = new CountingDownloadTaskStore(innerStore);
        var clock = new SystemClock();
        var historyService = DownloadHistoryService.CreateForSharedStore(store);
        using var tasks = new DownloadTaskApplicationService(store, historyService, clock);
        using var projections = new DownloadTaskProjectionStore(
            tasks,
            historyService,
            clock);
        var list = new DownloadListState();
        var queue = new RecordingDownloadTaskQueue();
        using var admission = CreateAdmission(list, tasks, projections, queue);
        var basePath = Path.Combine(_directory, "db-no-auto-suffix");
        await admission.AdmitAsync(CreateItem("first", basePath), true, TestContext.Current.CancellationToken);
        var rejected = CreateItem("rejected", basePath);

        await Assert.ThrowsAsync<IOException>(() => admission.AdmitAsync(
            rejected, false, TestContext.Current.CancellationToken));

        Assert.Equal(basePath, rejected.DownloadBase.FilePath);
        Assert.Single(list.Downloading);
        Assert.Single(queue.Enqueued);
        Assert.Equal(2, store.ReservationProbeCount);
        Assert.Equal(0, store.ReservationSnapshotCount);
        Assert.Equal(1, store.AddCallCount);
    }

    [Fact]
    public async Task DisjointResidualActionSharesTheOwnedBasePath()
    {
        Directory.CreateDirectory(_directory);
        using var store = CreateStore();
        var clock = new SystemClock();
        var historyService = DownloadHistoryService.CreateForSharedStore(store);
        using var tasks = new DownloadTaskApplicationService(store, historyService, clock);
        using var projections = new DownloadTaskProjectionStore(tasks, historyService, clock);
        var list = new DownloadListState();
        using var admission = CreateAdmission(
            list,
            tasks,
            projections,
            new RecordingDownloadTaskQueue());
        var basePath = Path.Combine(_directory, "shared-residual-output");
        var media = CreateItem("media", basePath, cid: 1001);
        media.DownloadBase.NeedDownloadContent =
            DownloadContentSelection.None with { Video = true };
        await admission.AdmitAsync(media, true, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            basePath + ".mp4",
            "owned-media",
            TestContext.Current.CancellationToken);
        var subtitle = CreateItem("subtitle", basePath, cid: 1001);
        subtitle.DownloadBase.NeedDownloadContent = DownloadContentSelection.None with
        {
            Subtitle = true,
            SelectedSubtitleTrackIds = [11]
        };

        await admission.AdmitAsync(
            subtitle,
            autoAddNumberSuffix: true,
            cancellationToken: TestContext.Current.CancellationToken,
            allowExistingBasePath: true);

        Assert.Equal(basePath, subtitle.DownloadBase.FilePath);
        Assert.Equal(2, list.Downloading.Count);
        Assert.Equal(2, (await tasks.GetUnfinishedAsync(
            TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task ResidualActionDoesNotOverwriteAnExistingMatchingSidecar()
    {
        Directory.CreateDirectory(_directory);
        using var store = CreateStore();
        var clock = new SystemClock();
        var historyService = DownloadHistoryService.CreateForSharedStore(store);
        using var tasks = new DownloadTaskApplicationService(store, historyService, clock);
        using var projections = new DownloadTaskProjectionStore(tasks, historyService, clock);
        using var admission = CreateAdmission(
            new DownloadListState(),
            tasks,
            projections,
            new RecordingDownloadTaskQueue());
        var basePath = Path.Combine(_directory, "foreign-sidecar-output");
        await File.WriteAllTextAsync(
            basePath + "_English.srt",
            "foreign-subtitle",
            TestContext.Current.CancellationToken);
        var subtitle = CreateItem("subtitle-foreign", basePath);
        subtitle.DownloadBase.NeedDownloadContent = DownloadContentSelection.None with
        {
            Subtitle = true,
            SelectedSubtitleTrackIds = [11]
        };

        await admission.AdmitAsync(
            subtitle,
            autoAddNumberSuffix: true,
            cancellationToken: TestContext.Current.CancellationToken,
            allowExistingBasePath: true);

        Assert.Equal(basePath + "(1)", subtitle.DownloadBase.FilePath);
    }

    [Fact]
    public async Task MetadataGeneratingMediaDoesNotOverwriteAnExistingNfo()
    {
        Directory.CreateDirectory(_directory);
        using var store = CreateStore();
        var clock = new SystemClock();
        var historyService = DownloadHistoryService.CreateForSharedStore(store);
        using var tasks = new DownloadTaskApplicationService(store, historyService, clock);
        using var projections = new DownloadTaskProjectionStore(tasks, historyService, clock);
        using var admission = CreateAdmission(
            new DownloadListState(),
            tasks,
            projections,
            new RecordingDownloadTaskQueue());
        var basePath = Path.Combine(_directory, "existing-nfo-output");
        await File.WriteAllTextAsync(
            basePath + ".nfo",
            "foreign-metadata",
            TestContext.Current.CancellationToken);
        var item = CreateItem("nfo-conflict", basePath);
        item.Metadata = new MovieMetadata { Title = "new metadata" };

        await admission.AdmitAsync(
            item,
            autoAddNumberSuffix: true,
            cancellationToken: TestContext.Current.CancellationToken,
            allowExistingBasePath: true);

        Assert.Equal(basePath + "(1)", item.DownloadBase.FilePath);
    }

    [Theory]
    [InlineData(DownloadActionClaim.Media, ".mp4")]
    [InlineData(DownloadActionClaim.Subtitle, "_English.srt")]
    [InlineData(DownloadActionClaim.DanmakuAss, ".ass")]
    [InlineData(DownloadActionClaim.DanmakuXml, ".xml")]
    [InlineData(DownloadActionClaim.Cover, ".Cover.png")]
    [InlineData(DownloadActionClaim.Nfo, ".nfo")]
    public void ExistingOutputClaimsUseCanonicalUnicodeNormalization(
        DownloadActionClaim claim,
        string outputSuffix)
    {
        Directory.CreateDirectory(_directory);
        var requestedBasePath = Path.Combine(_directory, "caf\u00e9-output");
        var existingBasePath = Path.Combine(_directory, "cafe\u0301-output");
        File.WriteAllText(existingBasePath + outputSuffix, "occupied");

        var hasConflict = DownloadOutputPathResolver.HasExistingOutputClaimConflict(
            requestedBasePath,
            new DownloadActionClaims(claim));

        Assert.True(hasConflict);
    }

    [Fact]
    public async Task CollisionResolutionPreservesRawCandidateSpelling()
    {
        var basePath = Path.Combine(_directory, "cafe\u0301-output");

        var resolved = await DownloadOutputPathResolver.ResolveAdmissionCollisionAsync(
            basePath,
            autoAddNumberSuffix: true,
            static (_, _) => Task.FromResult(false),
            static _ => Task.FromResult<IReadOnlyList<string>>([]),
            TestContext.Current.CancellationToken);

        Assert.Equal(Path.GetFullPath(basePath), resolved, ignoreCase: false);
    }

    [Fact]
    public async Task FallbackRechecksOriginalBasenameFromCurrentSnapshot()
    {
        var basePath = Path.Combine(_directory, "released-between-observations");
        var resolved = await DownloadOutputPathResolver.ResolveAdmissionCollisionAsync(
            basePath,
            autoAddNumberSuffix: true,
            static (_, _) => Task.FromResult(true),
            static _ => Task.FromResult<IReadOnlyList<string>>([]),
            TestContext.Current.CancellationToken);

        Assert.Equal(basePath, resolved);
    }

    [Fact]
    public async Task AdmissionFreezesPhysicalPathBeforeReservationPersistenceProjectionAndQueue()
    {
        Directory.CreateDirectory(_directory);
        using var innerStore = CreateStore();
        var store = new CountingDownloadTaskStore(innerStore);
        var clock = new SystemClock();
        var historyService = DownloadHistoryService.CreateForSharedStore(store);
        using var tasks = new DownloadTaskApplicationService(store, historyService, clock);
        using var projections = new DownloadTaskProjectionStore(
            tasks,
            historyService,
            clock);
        var listState = new DownloadListState();
        var queue = new AdmissionObservingQueue(listState, projections);
        var logicalBasePath = Path.Combine(_directory, "logical-alias", "cafe\u0301-output");
        var frozenBasePath = Path.Combine(_directory, "physical-target", "cafe\u0301-output");
        var resolver = new RecordingPhysicalOutputPathResolver(_ => frozenBasePath);
        using var admission = CreateAdmission(listState, tasks, projections, queue, resolver);
        var item = CreateItem("frozen", logicalBasePath);

        await admission.AdmitAsync(item, true, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        var persisted = Assert.Single(await tasks.GetUnfinishedAsync(
            TestContext.Current.CancellationToken));
        var taskId = new DownloadTaskId(item.DownloadBase.Id);
        Assert.Equal([logicalBasePath], resolver.Inputs);
        Assert.Equal(0, store.ReservationSnapshotCount);
        Assert.Equal(1, store.ReservationProbeCount);
        Assert.Equal(frozenBasePath, item.DownloadBase.FilePath, ignoreCase: false);
        Assert.Equal(frozenBasePath, persisted.Output.BasePath, ignoreCase: false);
        Assert.Equal(
            frozenBasePath,
            projections.GetRequiredSnapshot(taskId).Output.BasePath,
            ignoreCase: false);
        Assert.Same(item, Assert.Single(listState.Downloading));
        Assert.Equal(taskId, Assert.Single(queue.Enqueued));
        Assert.Equal([frozenBasePath], queue.ObservedPaths);
        Assert.DoesNotContain(
            logicalBasePath,
            new[] { item.DownloadBase.FilePath, persisted.Output.BasePath },
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task ResolverFailureHasNoPersistenceListOrQueueSideEffects()
    {
        Directory.CreateDirectory(_directory);
        using var store = CreateStore();
        var clock = new SystemClock();
        var historyService = DownloadHistoryService.CreateForSharedStore(store);
        using var tasks = new DownloadTaskApplicationService(store, historyService, clock);
        using var projections = new DownloadTaskProjectionStore(
            tasks,
            historyService,
            clock);
        var listState = new DownloadListState();
        var queue = new RecordingDownloadTaskQueue();
        var logicalBasePath = Path.Combine(_directory, "broken-alias", "output");
        var resolver = new RecordingPhysicalOutputPathResolver(
            _ => throw new IOException("Alias resolution failed."));
        using var admission = CreateAdmission(listState, tasks, projections, queue, resolver);
        var item = CreateItem("unresolved", logicalBasePath);

        await Assert.ThrowsAsync<IOException>(() => admission.AdmitAsync(
            item,
            true,
            TestContext.Current.CancellationToken));

        Assert.Equal([logicalBasePath], resolver.Inputs);
        Assert.Equal(logicalBasePath, item.DownloadBase.FilePath, ignoreCase: false);
        Assert.Empty(await tasks.GetUnfinishedAsync(TestContext.Current.CancellationToken));
        Assert.Empty(listState.Downloading);
        Assert.Empty(queue.Enqueued);
    }

    [Fact]
    public async Task PersistenceFailureDoesNotPublishToListOrQueue()
    {
        Directory.CreateDirectory(_directory);
        using var innerStore = CreateStore();
        var store = new CountingDownloadTaskStore(innerStore) { RejectAdds = true };
        var clock = new SystemClock();
        var historyService = DownloadHistoryService.CreateForSharedStore(store);
        using var tasks = new DownloadTaskApplicationService(store, historyService, clock);
        using var projections = new DownloadTaskProjectionStore(
            tasks,
            historyService,
            clock);
        var listState = new DownloadListState();
        var queue = new RecordingDownloadTaskQueue();
        using var admission = CreateAdmission(listState, tasks, projections, queue);
        var item = CreateItem("rejected", Path.Combine(_directory, "rejected-output"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => admission.AdmitAsync(
            item,
            true,
            TestContext.Current.CancellationToken));

        Assert.Empty(await innerStore.GetUnfinishedAsync(TestContext.Current.CancellationToken));
        Assert.Empty(listState.Downloading);
        Assert.Empty(queue.Enqueued);
    }

    [Fact]
    public async Task RuntimeUnavailableRejectsAdmissionBeforePersistence()
    {
        Directory.CreateDirectory(_directory);
        using var store = CreateStore();
        var clock = new SystemClock();
        var historyService = DownloadHistoryService.CreateForSharedStore(store);
        using var tasks = new DownloadTaskApplicationService(store, historyService, clock);
        using var projections = new DownloadTaskProjectionStore(
            tasks,
            historyService,
            clock);
        var listState = new DownloadListState();
        var gateway = new DownloadTaskQueueGateway();
        gateway.MarkFaulted(new InvalidOperationException("Synthetic bootstrap failure."));
        using var admission = CreateAdmission(
            listState,
            tasks,
            projections,
            gateway,
            runtimeAvailability: gateway);
        var item = CreateItem("runtime-unavailable", Path.Combine(_directory, "output"));

        await Assert.ThrowsAsync<DownloadRuntimeUnavailableException>(() => admission.AdmitAsync(
            item,
            true,
            TestContext.Current.CancellationToken));

        Assert.Empty(await tasks.GetUnfinishedAsync(TestContext.Current.CancellationToken));
        Assert.Empty(listState.Downloading);
    }

    [Fact]
    public async Task InitializingRuntimeOwnsAdmissionUntilBootstrapCompletes()
    {
        Directory.CreateDirectory(_directory);
        using var store = CreateStore();
        var clock = new SystemClock();
        var historyService = DownloadHistoryService.CreateForSharedStore(store);
        using var tasks = new DownloadTaskApplicationService(store, historyService, clock);
        using var projections = new DownloadTaskProjectionStore(
            tasks,
            historyService,
            clock);
        var listState = new DownloadListState();
        var gateway = new DownloadTaskQueueGateway();
        using var admission = CreateAdmission(
            listState,
            tasks,
            projections,
            gateway,
            runtimeAvailability: gateway);
        var item = CreateItem("runtime-initializing", Path.Combine(_directory, "output"));

        await admission.AdmitAsync(item, true, TestContext.Current.CancellationToken);

        var persisted = Assert.IsType<DownloadTask>(await tasks.FindAsync(
            new DownloadTaskId(item.DownloadBase.Id),
            TestContext.Current.CancellationToken));
        Assert.Equal(DownloadPhase.Queued, persisted.Phase);
        Assert.Same(item, Assert.Single(listState.Downloading));
        Assert.Equal(
            persisted.Id,
            Assert.Single(gateway.MarkFaulted(
                new InvalidOperationException("Synthetic terminal bootstrap failure."))));
    }

    [Fact]
    public async Task RuntimeFailureAfterPersistenceMarksCommittedTaskFailed()
    {
        Directory.CreateDirectory(_directory);
        using var store = CreateStore();
        var clock = new SystemClock();
        var historyService = DownloadHistoryService.CreateForSharedStore(store);
        using var tasks = new DownloadTaskApplicationService(store, historyService, clock);
        using var projections = new DownloadTaskProjectionStore(
            tasks,
            historyService,
            clock);
        var listState = new DownloadListState();
        using var admission = CreateAdmission(
            listState,
            tasks,
            projections,
            new RuntimeUnavailableQueue());
        var item = CreateItem("runtime-failed-after-commit", Path.Combine(_directory, "output"));

        await Assert.ThrowsAsync<DownloadRuntimeUnavailableException>(() => admission.AdmitAsync(
            item,
            true,
            TestContext.Current.CancellationToken));

        var persisted = Assert.IsType<DownloadTask>(await tasks.FindAsync(
            new DownloadTaskId(item.DownloadBase.Id),
            TestContext.Current.CancellationToken));
        Assert.Equal(DownloadPhase.Failed, persisted.Phase);
        Assert.Equal("download.runtime.unavailable", persisted.Failure?.Code);
        Assert.Equal(DownloadStatus.DownloadFailed, Assert.Single(listState.Downloading).Downloading.DownloadStatus);
    }

    [Fact]
    public async Task AliasesSharePhysicalCollisionSequenceAndRemainFrozenAfterRetarget()
    {
        Directory.CreateDirectory(_directory);
        using var store = CreateStore();
        var clock = new SystemClock();
        var historyService = DownloadHistoryService.CreateForSharedStore(store);
        using var tasks = new DownloadTaskApplicationService(store, historyService, clock);
        using var projections = new DownloadTaskProjectionStore(
            tasks,
            historyService,
            clock);
        var frozenBasePath = Path.Combine(_directory, "physical-target", "output");
        var currentTarget = frozenBasePath;
        var resolver = new RecordingPhysicalOutputPathResolver(_ => currentTarget);
        using var admission = CreateAdmission(
            new DownloadListState(),
            tasks,
            projections,
            new RecordingDownloadTaskQueue(),
            resolver);
        var first = CreateItem("first-alias", Path.Combine(_directory, "alias-a", "output"));
        var second = CreateItem("second-alias", Path.Combine(_directory, "alias-b", "output"));

        await admission.AdmitAsync(first, true, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        currentTarget = Path.Combine(_directory, "retargeted", "output");
        Assert.Equal(frozenBasePath, first.DownloadBase.FilePath, ignoreCase: false);
        Assert.Equal(
            frozenBasePath,
            projections.GetRequiredSnapshot(new DownloadTaskId(first.DownloadBase.Id)).Output.BasePath,
            ignoreCase: false);
        currentTarget = frozenBasePath;
        await admission.AdmitAsync(second, true, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(frozenBasePath, first.DownloadBase.FilePath, ignoreCase: false);
        Assert.Equal($"{frozenBasePath}(1)", second.DownloadBase.FilePath, ignoreCase: false);
        Assert.Equal(2, resolver.Inputs.Count);
    }

    [Fact]
    public void CaseInsensitivePathKeyTreatsMacStyleCaseVariantsAsOneOutput()
    {
        var first = Path.Combine(_directory, "Video");
        var second = Path.Combine(_directory, "video");

        Assert.Equal(
            DownloadOutputPathKey.Create(first, ignoreCase: true),
            DownloadOutputPathKey.Create(second, ignoreCase: true));
        Assert.NotEqual(
            DownloadOutputPathKey.Create(first, ignoreCase: false),
            DownloadOutputPathKey.Create(second, ignoreCase: false));
    }

    [Fact]
    public void PlatformComparisonIsCaseInsensitiveOnWindowsAndMacOS()
    {
        var expected = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        Assert.Same(expected, DownloadOutputPathResolver.PlatformComparer);
    }

    [Fact]
    public async Task ActiveReservationHoleUsesFirstFreeSuffix()
    {
        Directory.CreateDirectory(_directory);
        using var store = CreateStore();
        var clock = new SystemClock();
        var historyService = DownloadHistoryService.CreateForSharedStore(store);
        using var tasks = new DownloadTaskApplicationService(store, historyService, clock);
        using var projections = new DownloadTaskProjectionStore(
            tasks,
            historyService,
            clock);
        using var admission = CreateAdmission(
            new DownloadListState(), tasks, projections, new RecordingDownloadTaskQueue());
        var basePath = Path.Combine(_directory, "video");
        await AdmitThreeWithHoleAsync(admission, basePath);

        var next = CreateItem("hole", basePath);
        await admission.AdmitAsync(next, true, TestContext.Current.CancellationToken);

        Assert.Equal($"{basePath}(2)", next.DownloadBase.FilePath);
    }

    [Fact]
    public async Task DiskOccupantBlocksReservationHole()
    {
        Directory.CreateDirectory(_directory);
        using var store = CreateStore();
        var clock = new SystemClock();
        var historyService = DownloadHistoryService.CreateForSharedStore(store);
        using var tasks = new DownloadTaskApplicationService(store, historyService, clock);
        using var projections = new DownloadTaskProjectionStore(
            tasks,
            historyService,
            clock);
        using var admission = CreateAdmission(
            new DownloadListState(), tasks, projections, new RecordingDownloadTaskQueue());
        var basePath = Path.Combine(_directory, "video");
        await AdmitThreeWithHoleAsync(admission, basePath);
        await File.WriteAllTextAsync(
            $"{basePath}(2).mp4", "foreign output", TestContext.Current.CancellationToken);

        var next = CreateItem("disk-hole", basePath);
        await admission.AdmitAsync(next, true, TestContext.Current.CancellationToken);

        Assert.Equal($"{basePath}(4)", next.DownloadBase.FilePath);
    }

    [Fact]
    public async Task ReopenedStoreStillUsesFirstFreeReservationHole()
    {
        Directory.CreateDirectory(_directory);
        var basePath = Path.Combine(_directory, "video");
        using (var firstStore = CreateStore())
        {
            var clock = new SystemClock();
            var firstHistoryService = DownloadHistoryService.CreateForSharedStore(firstStore);
            using var firstTasks = new DownloadTaskApplicationService(firstStore, firstHistoryService, clock);
            using var firstProjections = new DownloadTaskProjectionStore(
                firstTasks,
                firstHistoryService,
                clock);
            using var firstAdmission = CreateAdmission(
                new DownloadListState(), firstTasks, firstProjections, new RecordingDownloadTaskQueue());
            await AdmitThreeWithHoleAsync(firstAdmission, basePath);
        }

        using var reopenedStore = CreateStore();
        var reopenedClock = new SystemClock();
        var reopenedHistoryService = DownloadHistoryService.CreateForSharedStore(reopenedStore);
        using var reopenedTasks = new DownloadTaskApplicationService(reopenedStore, reopenedHistoryService, reopenedClock);
        using var reopenedProjections = new DownloadTaskProjectionStore(
            reopenedTasks,
            reopenedHistoryService,
            reopenedClock);
        using var reopenedAdmission = CreateAdmission(
            new DownloadListState(), reopenedTasks, reopenedProjections, new RecordingDownloadTaskQueue());
        var next = CreateItem("after-reopen", basePath);
        await reopenedAdmission.AdmitAsync(next, true, TestContext.Current.CancellationToken);

        Assert.Equal($"{basePath}(2)", next.DownloadBase.FilePath);
    }

    [Fact]
    public async Task RepeatedAdmissionsUseOnePointProbeAndOnlyCollisionSnapshots()
    {
        Directory.CreateDirectory(_directory);
        using var innerStore = CreateStore();
        var store = new CountingDownloadTaskStore(innerStore);
        var clock = new SystemClock();
        var historyService = DownloadHistoryService.CreateForSharedStore(store);
        using var tasks = new DownloadTaskApplicationService(store, historyService, clock);
        using var projections = new DownloadTaskProjectionStore(
            tasks,
            historyService,
            clock);
        using var admission = CreateAdmission(
            new DownloadListState(),
            tasks,
            projections,
            new RecordingDownloadTaskQueue());

        const int admissionCount = 64;
        var basePath = Path.Combine(_directory, "same-output");
        for (var index = 0; index < admissionCount; index++)
        {
            var item = CreateItem($"task-{index}", basePath);
            await admission.AdmitAsync(item, true, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            Assert.Equal(index == 0 ? basePath : $"{basePath}({index})", item.DownloadBase.FilePath);
        }

        Assert.Equal(0, store.GetUnfinishedCallCount);
        Assert.Equal(admissionCount - 1, store.ReservationSnapshotCount);
        Assert.Equal(admissionCount, store.ReservationProbeCount);
        Assert.Equal(admissionCount, store.AddCallCount);
    }

    [Fact]
    public async Task DistinctAdmissionsUseOnlyOriginalBasenamePointQueries()
    {
        Directory.CreateDirectory(_directory);
        using var innerStore = CreateStore();
        var store = new CountingDownloadTaskStore(innerStore);
        var clock = new SystemClock();
        var historyService = DownloadHistoryService.CreateForSharedStore(store);
        using var tasks = new DownloadTaskApplicationService(store, historyService, clock);
        using var projections = new DownloadTaskProjectionStore(
            tasks,
            historyService,
            clock);
        using var admission = CreateAdmission(
            new DownloadListState(), tasks, projections, new RecordingDownloadTaskQueue());

        const int admissionCount = 64;
        for (var index = 0; index < admissionCount; index++)
        {
            var basePath = Path.Combine(_directory, $"distinct-{index:D3}");
            var item = CreateItem($"distinct-task-{index}", basePath);
            await admission.AdmitAsync(item, true, TestContext.Current.CancellationToken);
            Assert.Equal(basePath, item.DownloadBase.FilePath);
        }

        Assert.Equal(admissionCount, store.ReservationProbeCount);
        Assert.Equal(0, store.ReservationSnapshotCount);
        Assert.Equal(admissionCount, store.AddCallCount);
        Assert.Equal(0, store.GetUnfinishedCallCount);
    }

    [Fact]
    public async Task LegacyNullKeyWithAlternateUnicodeSpellingEntersSnapshotFallback()
    {
        Directory.CreateDirectory(_directory);
        using var innerStore = CreateStore();
        var store = new CountingDownloadTaskStore(innerStore);
        var clock = new SystemClock();
        var historyService = DownloadHistoryService.CreateForSharedStore(store);
        using var tasks = new DownloadTaskApplicationService(store, historyService, clock);
        using var projections = new DownloadTaskProjectionStore(
            tasks,
            historyService,
            clock);
        using var admission = CreateAdmission(
            new DownloadListState(), tasks, projections, new RecordingDownloadTaskQueue());
        var decomposed = Path.Combine(_directory, "cafe\u0301-legacy-admission");
        var composed = Path.Combine(_directory, "caf\u00e9-legacy-admission");
        await admission.AdmitAsync(
            CreateItem("legacy-first", decomposed), true, TestContext.Current.CancellationToken);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "download.db"),
            Pooling = false
        }.ToString()))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE download_base SET output_reservation_key = NULL
                WHERE id = 'legacy-first'
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var next = CreateItem("legacy-next", composed);
        await admission.AdmitAsync(next, true, TestContext.Current.CancellationToken);

        Assert.Equal($"{composed}(1)", next.DownloadBase.FilePath);
        Assert.Equal(2, store.ReservationProbeCount);
        Assert.Equal(1, store.ReservationSnapshotCount);
        Assert.Equal(2, store.AddCallCount);
    }

    private static async Task AdmitThreeWithHoleAsync(
        DownloadTaskAdmissionService admission,
        string basePath)
    {
        foreach (var (id, path) in new[]
        {
            ("zero", basePath),
            ("one", $"{basePath}(1)"),
            ("three", $"{basePath}(3)")
        })
        {
            var item = CreateItem(id, path);
            await admission.AdmitAsync(item, true, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            Assert.Equal(path, item.DownloadBase.FilePath);
        }
    }

    private SqliteDownloadTaskStore CreateStore()
    {
        return new SqliteDownloadTaskStore(
            new SqliteDownloadTaskStoreOptions(Path.Combine(_directory, "download.db")),
            new SystemClock());
    }

    private static DownloadTaskAdmissionService CreateAdmission(
        DownloadListState listState,
        IDownloadTaskApplicationService tasks,
        DownloadTaskProjectionStore projections,
        IDownloadTaskQueue queue,
        IPhysicalOutputPathResolver? resolver = null,
        IDownloadRuntimeAvailability? runtimeAvailability = null)
    {
        return new DownloadTaskAdmissionService(
            listState,
            tasks,
            projections,
            new DownloadTaskStateWriter(tasks),
            queue,
            runtimeAvailability ?? new ReadyDownloadRuntimeAvailability(),
            resolver ?? new RecordingPhysicalOutputPathResolver(static path => path));
    }

    private static DownloadingItem CreateItem(string id, string basePath, long cid = 0)
    {
        return new DownloadingItem
        {
            DownloadBase = new DownloadBase
            {
                Id = id,
                Bvid = $"BV-{id}",
                Cid = cid,
                MainTitle = id,
                Name = id,
                FilePath = basePath
            },
            Downloading = new Downloading
            {
                Id = id,
                DownloadStatus = DownloadStatus.WaitForDownload
            }
        };
    }

    private sealed class CountingDownloadTaskStore(IDownloadTaskStore inner) :
        IDownloadTaskStore,
        IDownloadHistoryStore,
        IDownloadCompletionStore
    {
        private readonly IDownloadHistoryStore _history = inner as IDownloadHistoryStore ??
            throw new ArgumentException("The inner store must provide history storage.", nameof(inner));
        private readonly IDownloadCompletionStore _completion = inner as IDownloadCompletionStore ??
            throw new ArgumentException("The inner store must provide completion storage.", nameof(inner));
        public bool RejectAdds { get; init; }

        public int GetUnfinishedCallCount { get; private set; }

        public int ReservationProbeCount { get; private set; }

        public int ReservationSnapshotCount { get; private set; }

        public int AddCallCount { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken) =>
            inner.InitializeAsync(cancellationToken);

        public Task<OperationResult> AddAsync(
            DownloadTask task,
            CancellationToken cancellationToken)
        {
            AddCallCount++;
            return RejectAdds
                ? Task.FromResult(OperationResult.Failure(new OperationError(
                    "test.persistence_rejected",
                    "Persistence rejected the task.")))
                : inner.AddAsync(task, cancellationToken);
        }

        public Task<OperationResult> AddHistoryAsync(
            DownloadHistoryRecord history,
            CancellationToken cancellationToken) =>
            _history.AddHistoryAsync(history, cancellationToken);

        public Task<OperationResult> UpdateAsync(
            DownloadTask task,
            long expectedVersion,
            CancellationToken cancellationToken) =>
            inner.UpdateAsync(task, expectedVersion, cancellationToken);

        public Task<OperationResult> CompleteAsync(
            DownloadTask task,
            DownloadHistoryRecord history,
            long expectedVersion,
            CancellationToken cancellationToken) =>
            _completion.CompleteAsync(task, history, expectedVersion, cancellationToken);

        public Task<OperationResult> UpdateProgressAsync(
            DownloadProgressWrite progressWrite,
            CancellationToken cancellationToken) =>
            inner.UpdateProgressAsync(progressWrite, cancellationToken);

        public Task<DownloadTask?> FindAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken) => inner.FindAsync(taskId, cancellationToken);

        public Task<IReadOnlyList<DownloadTask>> GetUnfinishedAsync(
            CancellationToken cancellationToken)
        {
            GetUnfinishedCallCount++;
            return inner.GetUnfinishedAsync(cancellationToken);
        }

        public Task<bool> IsOutputPathReservedAsync(
            string basePath,
            bool ignoreCase,
            CancellationToken cancellationToken)
        {
            ReservationProbeCount++;
            return inner.IsOutputPathReservedAsync(basePath, ignoreCase, cancellationToken);
        }

        public Task<IReadOnlyList<string>> GetActiveOutputReservationKeysAsync(
            bool ignoreCase,
            CancellationToken cancellationToken)
        {
            ReservationSnapshotCount++;
            return inner.GetActiveOutputReservationKeysAsync(ignoreCase, cancellationToken);
        }

        public Task<bool> HasOutputClaimConflictAsync(
            string basePath,
            long requestedCid,
            DownloadActionClaims requestedClaims,
            bool ignoreCase,
            CancellationToken cancellationToken)
        {
            ReservationProbeCount++;
            return inner.HasOutputClaimConflictAsync(
                basePath,
                requestedCid,
                requestedClaims,
                ignoreCase,
                cancellationToken);
        }

        public Task<DownloadHistoryPage> GetHistoryPageAsync(
            DownloadHistoryCursor? cursor,
            int pageSize,
            CancellationToken cancellationToken) =>
            _history.GetHistoryPageAsync(cursor, pageSize, cancellationToken);

        public Task<OperationResult> DeleteAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken) => inner.DeleteAsync(taskId, cancellationToken);

        public Task<OperationResult> DeleteHistoryAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken) =>
            _history.DeleteHistoryAsync(taskId, cancellationToken);

        public Task<OperationResult> ClearHistoryAsync(CancellationToken cancellationToken) =>
            _history.ClearHistoryAsync(cancellationToken);

        public Task<IReadOnlyList<QuarantinedDownloadRecord>> GetQuarantinedRecordsAsync(
            CancellationToken cancellationToken) => inner.GetQuarantinedRecordsAsync(cancellationToken);
    }

    private sealed class RecordingPhysicalOutputPathResolver(Func<string, string> resolve)
        : IPhysicalOutputPathResolver
    {
        public List<string> Inputs { get; } = [];

        public string ResolvePhysicalBasePath(string logicalBasePath)
        {
            Inputs.Add(logicalBasePath);
            return resolve(logicalBasePath);
        }
    }

    private sealed class AdmissionObservingQueue(
        DownloadListState listState,
        DownloadTaskProjectionStore projections) : IDownloadTaskQueue
    {
        public List<DownloadTaskId> Enqueued { get; } = [];

        public List<string> ObservedPaths { get; } = [];

        public Task EnqueueAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var listItem = Assert.Single(
                listState.Downloading,
                item => item.DownloadBase.Id == taskId.Value);
            Assert.Equal(
                listItem.DownloadBase.FilePath,
                projections.GetRequiredSnapshot(taskId).Output.BasePath,
                ignoreCase: false);
            Enqueued.Add(taskId);
            ObservedPaths.Add(listItem.DownloadBase.FilePath);
            return Task.CompletedTask;
        }

        public Task<bool> CancelAsync(DownloadTaskId taskId) => Task.FromResult(true);
    }

    private sealed class RuntimeUnavailableQueue : IDownloadTaskQueue
    {
        public Task EnqueueAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken = default)
        {
            throw new DownloadRuntimeUnavailableException("Synthetic runtime failure.");
        }

        public Task<bool> CancelAsync(DownloadTaskId taskId) => Task.FromResult(false);
    }

    public void Dispose()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "download.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 5
        }.ToString());
        SqliteConnection.ClearPool(connection);

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
