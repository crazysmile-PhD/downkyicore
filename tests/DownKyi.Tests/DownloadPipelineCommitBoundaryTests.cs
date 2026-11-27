using System.Collections.Specialized;
using DownKyi.Application.Downloads;
using DownKyi.Application.Settings;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
using DownKyi.Infrastructure.Settings;
using DownKyi.Infrastructure.Time;
using DownKyi.Models;
using DownKyi.Platform;
using DownKyi.Services.Download;
using DownKyi.ViewModels.DownloadManager;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class DownloadPipelineCommitBoundaryTests
{
    [Theory]
    [InlineData("artifact")]
    [InlineData("mux")]
    [InlineData("validation")]
    public async Task PreCommitFailurePreservesSourcesAndRetryCheckpoint(string failedStage)
    {
        using var harness = await CommitBoundaryHarness.CreateAsync().ConfigureAwait(true);

        var run = await DownloadPipeline.ExecuteStagesAsync(
            [new FailureStage(failedStage), harness.FinalizeStage],
            harness.Context,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(run.Result.IsSuccess);
        Assert.Equal(failedStage, run.FailedStage);
        harness.AssertSourcesAndSidecarsExist();
        Assert.Equal(DownloadPhase.Downloading, harness.Store.Current?.Phase);
    }

    [Fact]
    public async Task ValidationFailureDeletesUnpublishedArtifactsAndFailsWithoutCompletion()
    {
        using var harness = await CommitBoundaryHarness.CreateAsync().ConfigureAwait(true);
        await harness.StateWriter.ClaimTransferFileAsync(
            harness.Context.TaskId,
            "video-source",
            Path.GetFileName(harness.Video),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        await harness.StateWriter.CompleteTransferFileAsync(
            harness.Context.TaskId,
            "video-source",
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        var pipeline = harness.CreatePipeline(new ValidationFailureStage());

        await pipeline.ExecuteAsync(
            harness.Context.TaskId,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(File.Exists(harness.Output));
        Assert.False(File.Exists(harness.PublishedOutput));
        harness.AssertSourcesAndSidecarsDeleted();
        Assert.Equal(DownloadPhase.Failed, harness.Store.Current?.Phase);
        Assert.Empty(harness.Store.Current!.Plan.TransferFiles);
        Assert.Empty(harness.Store.Current.Transfer.CompletedFileKeys);
        Assert.Null(harness.Store.History);
    }

    [Fact]
    public async Task DurableCompletionFailurePreservesSourcesAndRetryCheckpoint()
    {
        using var harness = await CommitBoundaryHarness.CreateAsync(
            rejectCompletion: true).ConfigureAwait(true);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.FinalizeStage.ExecuteAsync(
                harness.Context,
                TestContext.Current.CancellationToken)).ConfigureAwait(true);

        harness.AssertSourcesAndSidecarsExist();
        Assert.Equal(DownloadPhase.Downloading, harness.Store.Current?.Phase);
    }

    [Fact]
    public async Task FinalizeRejectsUnvalidatedArtifactsWithoutPublishing()
    {
        using var harness = await CommitBoundaryHarness.CreateAsync(
            markArtifactsValidated: false).ConfigureAwait(true);

        var result = await harness.FinalizeStage.ExecuteAsync(
            harness.Context,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.finalize.unvalidated", result.Error?.Code);
        Assert.Equal(OperationErrorKind.Validation, result.Error?.Kind);
        Assert.True(File.Exists(harness.Output));
        Assert.False(File.Exists(harness.PublishedOutput));
        Assert.Equal(DownloadPhase.Downloading, harness.Store.Current?.Phase);
    }

    [Fact]
    public async Task PublishRejectsArtifactThatBecameInvalidAfterValidation()
    {
        using var harness = await CommitBoundaryHarness.CreateAsync().ConfigureAwait(true);
        await File.WriteAllBytesAsync(
            harness.Output,
            [],
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        var result = await harness.FileService.PublishAsync(
            harness.Context,
            "media",
            harness.Output,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.publish.invalid", result.Error?.Code);
        Assert.Equal(OperationErrorKind.Validation, result.Error?.Kind);
        Assert.True(File.Exists(harness.Output));
        Assert.False(File.Exists(harness.PublishedOutput));
        Assert.Equal(DownloadPhase.Downloading, harness.Store.Current?.Phase);
    }

    [Fact]
    public async Task PublishingStateFailureNeverMovesCompletedStaging()
    {
        using var harness = await CommitBoundaryHarness.CreateAsync(
            rejectPublishingStart: true).ConfigureAwait(true);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.FinalizeStage.ExecuteAsync(
                harness.Context, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.True(File.Exists(harness.Output));
        Assert.False(File.Exists(harness.PublishedOutput));
        Assert.Null(harness.Store.Current?.Output.PublishingArtifact);
        harness.AssertSourcesAndSidecarsExist();
    }

    [Fact]
    public async Task CommittedCompletionCleansOnlyTransferInputsAndSidecars()
    {
        using var harness = await CommitBoundaryHarness.CreateAsync().ConfigureAwait(true);
        harness.Lists.SortDownloaded(DownloadFinishedSort.DownloadDesc);
        var resets = 0;
        ((INotifyCollectionChanged)harness.Lists.Downloaded).CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Reset) resets++;
        };

        var result = await harness.FinalizeStage.ExecuteAsync(
            harness.Context,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.IsSuccess);
        harness.AssertSourcesAndSidecarsDeleted();
        Assert.True(File.Exists(harness.PublishedOutput));
        Assert.Null(harness.Store.Current);
        Assert.NotNull(harness.Store.History);
        Assert.Equal(harness.PublishedOutput,
            harness.Store.History.PublishedArtifacts["media"]);
        Assert.Empty(harness.Lists.Downloading);
        Assert.Single(harness.Lists.Downloaded);
        Assert.Equal(0, resets);
        Assert.Throws<InvalidOperationException>(() =>
            harness.Projections.GetRequiredDownloadingProjection(harness.Context.TaskId));
    }

    [Fact]
    public async Task FinalizePublishesValidatedAuxiliaryArtifactsOnlyAtCommitBoundary()
    {
        using var harness = await CommitBoundaryHarness.CreateAsync().ConfigureAwait(true);
        var stagedNfo = Path.Combine(harness.Context.StagingDirectory!, "output.nfo");
        var publishedNfo = Path.Combine(Path.GetDirectoryName(harness.PublishedOutput)!, "output.nfo");
        await File.WriteAllTextAsync(
            stagedNfo,
            "<movie><title>validated</title></movie>",
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        harness.Context.RecordUnpublishedArtifact("nfo", stagedNfo);
        harness.Context.MarkArtifactsValidated();
        Assert.False(File.Exists(publishedNfo));

        var result = await harness.FinalizeStage.ExecuteAsync(
            harness.Context,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(File.Exists(publishedNfo));
        Assert.Equal(publishedNfo, harness.Store.History?.PublishedArtifacts["nfo"]);
    }

    [Fact]
    public async Task PublishCollisionPreservesForeignDestinationAndCompletedStaging()
    {
        using var harness = await CommitBoundaryHarness.CreateAsync().ConfigureAwait(true);
        var foreignBytes = new byte[] { 91, 0, 255, 17 };
        await File.WriteAllBytesAsync(harness.PublishedOutput, foreignBytes, TestContext.Current.CancellationToken);

        var result = await harness.FinalizeStage.ExecuteAsync(
            harness.Context, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.publish.collision", result.Error?.Code);
        Assert.Equal(foreignBytes, await File.ReadAllBytesAsync(harness.PublishedOutput, TestContext.Current.CancellationToken));
        Assert.Equal(new byte[] { 7, 8, 9 }, await File.ReadAllBytesAsync(harness.Output, TestContext.Current.CancellationToken));
        Assert.Equal(DownloadPhase.Downloading, harness.Store.Current?.Phase);
        harness.AssertSourcesAndSidecarsExist();
    }

    [Fact]
    public async Task CollisionRetryPublishesExistingStagingWithoutRepeatingEarlierStages()
    {
        using var harness = await CommitBoundaryHarness.CreateAsync().ConfigureAwait(true);
        await File.WriteAllBytesAsync(harness.PublishedOutput, [91, 0, 255, 17], TestContext.Current.CancellationToken);
        var first = await harness.FinalizeStage.ExecuteAsync(
            harness.Context, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.False(first.IsSuccess);
        File.Delete(harness.PublishedOutput); // Simulates the external owner moving its file away.

        var retry = await harness.FinalizeStage.ExecuteAsync(
            harness.Context, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(retry.IsSuccess);
        Assert.Equal(new byte[] { 7, 8, 9 }, await File.ReadAllBytesAsync(harness.PublishedOutput, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(harness.Output));
        Assert.Null(harness.Store.Current);
        Assert.NotNull(harness.Store.History);
        Assert.Equal(harness.PublishedOutput,
            harness.Store.History.PublishedArtifacts["media"]);
        harness.AssertSourcesAndSidecarsDeleted();
    }

    private sealed class FailureStage(string name) : IDownloadPipelineStage
    {
        public string Name { get; } = name;

        public Task<OperationResult<DownloadStageResult>> ExecuteAsync(
            DownloadExecutionContext context,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(context);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(DownloadStageResult.Failure(
                "test.pre-commit.failure",
                "Synthetic pre-commit failure."));
        }
    }

    private sealed class ValidationFailureStage : IDownloadPipelineStage
    {
        public string Name => "validation";

        public Task<OperationResult<DownloadStageResult>> ExecuteAsync(
            DownloadExecutionContext context,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(context);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(DownloadStageResult.Failure(
                "download.validate.synthetic-corruption",
                "Synthetic corrupt staged product.",
                OperationErrorKind.Validation));
        }
    }

    private sealed class CommitBoundaryHarness : IDisposable
    {
        private readonly string _directory;
        private readonly SettingsStore _settings;
        private readonly DownloadTaskApplicationService _tasks;
        private readonly DownloadTaskProjectionStore _projectionStore;

        private CommitBoundaryHarness(
            string directory,
            SettingsStore settings,
            DownloadTaskApplicationService tasks,
            DownloadTaskProjectionStore projectionStore,
            CommitBoundaryStore store,
            DownloadListState lists,
            DownloadExecutionContext context,
            FinalizeStage finalizeStage,
            DownloadTaskStateWriter stateWriter,
            DownloadTaskFileService fileService,
            string audio,
            string video,
            string output,
            string publishedOutput,
            DownloadTaskStaging staging)
        {
            _directory = directory;
            _settings = settings;
            _tasks = tasks;
            _projectionStore = projectionStore;
            Store = store;
            Lists = lists;
            Context = context;
            FinalizeStage = finalizeStage;
            StateWriter = stateWriter;
            FileService = fileService;
            Audio = audio;
            Video = video;
            Output = output;
            PublishedOutput = publishedOutput;
            Staging = staging;
        }

        public CommitBoundaryStore Store { get; }

        public DownloadListState Lists { get; }

        public DownloadTaskProjectionStore Projections => _projectionStore;

        public DownloadExecutionContext Context { get; }

        public FinalizeStage FinalizeStage { get; }

        public DownloadTaskStateWriter StateWriter { get; }

        public DownloadTaskFileService FileService { get; }

        public string Audio { get; }

        public string Video { get; }

        public string Output { get; }

        public string PublishedOutput { get; }

        public DownloadTaskStaging Staging { get; }

        public static async Task<CommitBoundaryHarness> CreateAsync(
            bool rejectCompletion = false,
            bool rejectPublishingStart = false,
            bool markArtifactsValidated = true)
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "downkyi-pipeline-commit",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var settings = new SettingsStore(Path.Combine(directory, "settings.json"));
            var store = new CommitBoundaryStore(rejectCompletion, rejectPublishingStart);
            var clock = new SystemClock();
            var historyService = DownloadHistoryService.CreateForSharedStore(store);
            var tasks = new DownloadTaskApplicationService(store, historyService, clock);
            var projectionStore = new DownloadTaskProjectionStore(
                tasks,
                historyService);
            var stateWriter = new DownloadTaskStateWriter(tasks);
            var taskId = new DownloadTaskId(Guid.NewGuid().ToString("N"));
            var downloadBase = new DownloadBase
            {
                Id = taskId.Value,
                FilePath = Path.Combine(directory, "output")
            };
            downloadBase.NeedDownloadContent = DownloadContentSelection.None with
            {
                Audio = true,
                Video = true
            };
            var downloading = new DownloadingItem
            {
                DownloadBase = downloadBase,
                Downloading = new Downloading
                {
                    Id = taskId.Value,
                    DownloadBase = downloadBase,
                    DownloadStatus = DownloadStatus.WaitForDownload
                }
            };
            await projectionStore.AddDownloadingAsync(tasks,
                downloading,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            await stateWriter.StartAsync(
                taskId,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            var lists = new DownloadListState();
            lists.AddDownloading(downloading);
            var context = DownloadExecutionContextTestFactory.Create(
                downloading,
                settings.Current);
            var staging = new DownloadTaskStaging(NullLogger<DownloadTaskStaging>.Instance);
            context.StagingDirectory = staging.GetDirectory(
                taskId, downloadBase.FilePath,
                projectionStore.GetRequiredSnapshot(taskId).Output.StagingToken);
            Directory.CreateDirectory(context.StagingDirectory);
            var audio = CreateTransferFile(context.StagingDirectory, "audio.m4s");
            var video = CreateTransferFile(context.StagingDirectory, "video.m4s");
            var output = context.WorkingBasePath + ".mp4";
            var publishedOutput = Path.Combine(directory, "output.mp4");
            File.WriteAllBytes(output, [7, 8, 9]);
            context.AudioFile = audio;
            context.VideoFile = video;
            context.OutputMedia = output;
            context.MediaSucceeded = true;
            if (markArtifactsValidated)
            {
                context.MarkArtifactsValidated();
            }
            var fileService = new DownloadTaskFileService(
                NullLogger<DownloadTaskFileService>.Instance,
                staging,
                stateWriter);
            var finalizeStage = new FinalizeStage(
                projectionStore,
                stateWriter,
                new DownloadCompletionProjector(
                    lists,
                    projectionStore,
                    new ImmediateUiDispatcher()),
                fileService,
                TimeProvider.System,
                NullLogger<FinalizeStage>.Instance);
            return new CommitBoundaryHarness(
                directory,
                settings,
                tasks,
                projectionStore,
                store,
                lists,
                context,
                finalizeStage,
                stateWriter,
                fileService,
                audio,
                video,
                output,
                publishedOutput,
                staging);
        }

        public DownloadPipeline CreatePipeline(params IDownloadPipelineStage[] stages)
        {
            return new DownloadPipeline(
                new DownloadExecutionContextFactory(_projectionStore, _settings, Staging),
                stages,
                StateWriter,
                FileService,
                new DownloadTaskShutdownRecovery(_tasks, StateWriter),
                NullLogger.Instance);
        }

        public void AssertSourcesAndSidecarsExist()
        {
            Assert.All(OwnedTransferPaths(), path => Assert.True(File.Exists(path), path));
        }

        public void AssertSourcesAndSidecarsDeleted()
        {
            Assert.All(OwnedTransferPaths(), path => Assert.False(File.Exists(path), path));
        }

        public void Dispose()
        {
            _projectionStore.Dispose();
            _tasks.Dispose();
            _settings.Dispose();
            Staging.CleanupCurrentSession();
            Directory.Delete(_directory, recursive: true);
            GC.SuppressFinalize(this);
        }

        private IEnumerable<string> OwnedTransferPaths()
        {
            foreach (var source in new[] { Audio, Video })
            {
                yield return source;
                yield return $"{source}.download";
            }
        }

        private static string CreateTransferFile(string directory, string name)
        {
            var path = Path.Combine(directory, name);
            File.WriteAllBytes(path, [1, 2, 3]);
            File.WriteAllBytes($"{path}.download", [5]);
            return path;
        }
    }

    private sealed class ImmediateUiDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class CommitBoundaryStore(
        bool rejectCompletion,
        bool rejectPublishingStart) :
        IDownloadTaskStore,
        IDownloadHistoryStore,
        IDownloadCompletionStore
    {
        public DownloadTask? Current { get; private set; }

        public DownloadHistoryRecord? History { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<OperationResult> AddAsync(
            DownloadTask task,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Current = task;
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> AddHistoryAsync(
            DownloadHistoryRecord history,
            CancellationToken cancellationToken)
        {
            History = history;
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> UpdateAsync(
            DownloadTask task,
            long expectedVersion,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Current == null || Current.Version != expectedVersion)
            {
                return Task.FromResult(OperationResult.Failure(
                    new OperationError(
                        "download.store.conflict",
                        "Version conflict.",
                        OperationErrorKind.Conflict)));
            }

            if (rejectCompletion && task.Phase == DownloadPhase.Completed)
            {
                return Task.FromResult(OperationResult.Failure(
                    OperationError.Unexpected(
                        "download.store.synthetic-completion-failure",
                        "Synthetic completion persistence failure.")));
            }

            if (rejectPublishingStart && task.Output.PublishingArtifact != null &&
                Current.Output.PublishingArtifact == null)
            {
                return Task.FromResult(OperationResult.Failure(
                    OperationError.Unexpected(
                        "download.store.synthetic-publishing-failure",
                        "Synthetic publishing persistence failure.")));
            }

            Current = task;
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> CompleteAsync(
            DownloadTask task,
            DownloadHistoryRecord history,
            long expectedVersion,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Current == null || Current.Version != expectedVersion)
            {
                return Task.FromResult(OperationResult.Failure(
                    new OperationError(
                        "download.store.conflict",
                        "Version conflict.",
                        OperationErrorKind.Conflict)));
            }

            if (rejectCompletion)
            {
                return Task.FromResult(OperationResult.Failure(
                    OperationError.Unexpected(
                        "download.store.synthetic-completion-failure",
                        "Synthetic completion persistence failure.")));
            }

            Current = null;
            History = history;
            return Task.FromResult(OperationResult.Success());
        }

        public Task<DownloadTask?> FindAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Current?.Id == taskId ? Current : null);
        }

        public Task<IReadOnlyList<DownloadTask>> GetUnfinishedAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DownloadTask>>(
                Current == null || Current.Phase == DownloadPhase.Completed ? [] : [Current]);

        public Task<bool> IsOutputPathReservedAsync(
            string basePath,
            bool ignoreCase,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(false);
        }

        public Task<IReadOnlyList<string>> GetActiveOutputReservationKeysAsync(
            bool ignoreCase,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<DownloadHistoryPage> GetHistoryPageAsync(
            DownloadHistoryCursor? cursor,
            int pageSize,
            CancellationToken cancellationToken,
            DownloadFinishedSort sort = DownloadFinishedSort.DownloadDesc) =>
            Task.FromResult(new DownloadHistoryPage(
                History == null ? [] : [History],
                null));

        public Task<OperationResult> DeleteAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Success());

        public Task<OperationResult> DeleteHistoryAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken)
        {
            History = null;
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> ClearHistoryAsync(CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Success());

        public Task<IReadOnlyList<QuarantinedDownloadRecord>> GetQuarantinedRecordsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<QuarantinedDownloadRecord>>([]);
    }
}
