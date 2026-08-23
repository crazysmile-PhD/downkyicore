using DownKyi.Application.Downloads;
using DownKyi.Core.Settings;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
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
    public async Task CommittedCompletionCleansOnlyTransferInputsAndSidecars()
    {
        using var harness = await CommitBoundaryHarness.CreateAsync().ConfigureAwait(true);

        var result = await harness.FinalizeStage.ExecuteAsync(
            harness.Context,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.IsSuccess);
        harness.AssertSourcesAndSidecarsDeleted();
        Assert.True(File.Exists(harness.Output));
        Assert.Equal(DownloadPhase.Completed, harness.Store.Current?.Phase);
        Assert.Empty(harness.Lists.Downloading);
        Assert.Single(harness.Lists.Downloaded);
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
            string audio,
            string video,
            string output)
        {
            _directory = directory;
            _settings = settings;
            _tasks = tasks;
            _projectionStore = projectionStore;
            Store = store;
            Lists = lists;
            Context = context;
            FinalizeStage = finalizeStage;
            Audio = audio;
            Video = video;
            Output = output;
        }

        public CommitBoundaryStore Store { get; }

        public DownloadListState Lists { get; }

        public DownloadExecutionContext Context { get; }

        public FinalizeStage FinalizeStage { get; }

        public string Audio { get; }

        public string Video { get; }

        public string Output { get; }

        public static async Task<CommitBoundaryHarness> CreateAsync(
            bool rejectCompletion = false)
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "downkyi-pipeline-commit",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var settings = new SettingsStore(Path.Combine(directory, "settings.json"));
            var store = new CommitBoundaryStore(rejectCompletion);
            var clock = new SystemClock();
            var tasks = new DownloadTaskApplicationService(store, clock);
            var projectionStore = new DownloadTaskProjectionStore(tasks, clock);
            var stateWriter = new DownloadTaskStateWriter(tasks);
            var taskId = new DownloadTaskId(Guid.NewGuid().ToString("N"));
            var downloadBase = new DownloadBase
            {
                Id = taskId.Value,
                FilePath = Path.Combine(directory, "output")
            };
            foreach (var key in downloadBase.NeedDownloadContent.Keys.ToArray())
            {
                downloadBase.NeedDownloadContent[key] = false;
            }

            downloadBase.NeedDownloadContent["downloadAudio"] = true;
            downloadBase.NeedDownloadContent["downloadVideo"] = true;
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
            await projectionStore.AddDownloadingAsync(
                downloading,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            await stateWriter.StartAsync(
                taskId,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            var lists = new DownloadListState();
            lists.AddDownloading(downloading);
            var context = new DownloadExecutionContext(
                taskId,
                downloading,
                settings.Current,
                static (_, token) => token.ThrowIfCancellationRequested());
            var audio = CreateTransferFile(directory, "audio.m4s");
            var video = CreateTransferFile(directory, "video.m4s");
            var output = Path.Combine(directory, "output.mp4");
            File.WriteAllBytes(output, [7, 8, 9]);
            context.AudioFile = audio;
            context.VideoFile = video;
            context.OutputMedia = output;
            context.MediaSucceeded = true;
            var fileService = new DownloadTaskFileService(
                new AriaRuntimeClientRegistry(),
                NullLogger<DownloadTaskFileService>.Instance);
            var finalizeStage = new FinalizeStage(
                projectionStore,
                stateWriter,
                new DownloadCompletionProjector(lists, new ImmediateUiDispatcher()),
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
                audio,
                video,
                output);
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
            Directory.Delete(_directory, recursive: true);
            GC.SuppressFinalize(this);
        }

        private IEnumerable<string> OwnedTransferPaths()
        {
            foreach (var source in new[] { Audio, Video })
            {
                yield return source;
                yield return $"{source}.aria2";
                yield return $"{source}.download";
            }
        }

        private static string CreateTransferFile(string directory, string name)
        {
            var path = Path.Combine(directory, name);
            File.WriteAllBytes(path, [1, 2, 3]);
            File.WriteAllBytes($"{path}.aria2", [4]);
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

    private sealed class CommitBoundaryStore(bool rejectCompletion) : IDownloadTaskStore
    {
        public DownloadTask? Current { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<OperationResult> AddAsync(
            DownloadTask task,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Current = task;
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

            Current = task;
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> UpdateProgressAsync(
            DownloadProgressWrite progressWrite,
            CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Success());

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

        public Task<DownloadHistoryPage> GetHistoryPageAsync(
            DownloadHistoryCursor? cursor,
            int pageSize,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DownloadHistoryPage(
                Current?.Phase == DownloadPhase.Completed ? [Current] : [],
                null));

        public Task<OperationResult> DeleteAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Success());

        public Task<OperationResult> ClearHistoryAsync(CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Success());

        public Task<IReadOnlyList<QuarantinedDownloadRecord>> GetQuarantinedRecordsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<QuarantinedDownloadRecord>>([]);
    }
}
