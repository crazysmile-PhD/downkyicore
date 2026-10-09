using DownKyi.Application.Downloads;
using DownKyi.Application.Time;
using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Core.FFmpeg;
using DownKyi.Core.Settings;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
using DownKyi.Infrastructure.Time;
using DownKyi.Models;
using DownKyi.Services.Download;
using DownKyi.ViewModels.DownloadManager;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class MuxFailureRecoveryTests
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 3)]
    [InlineData(true, 3)]
    public async Task DurlMuxPassesFinalizedAudioIntentToFfmpeg(
        bool needsAudio,
        int segmentCount)
    {
        var requestedContent = DownloadContentSelection.None with
        {
            Audio = needsAudio,
            Video = true,
            MediaKind = DownloadMediaKind.Durl
        };
        var test = await MuxTestContext.CreateAsync(requestedContent).ConfigureAwait(true);
        await using var testLifetime = test.ConfigureAwait(true);
        await test.AddDurlSourcesAsync(segmentCount).ConfigureAwait(true);
        var stage = test.CreateStage(new FfmpegOperationResult(
            succeeded: true,
            outputPath: test.Execution.WorkingBasePath + ".mp4",
            failureReason: null,
            duration: TimeSpan.Zero));

        var result = await stage.ExecuteAsync(
            test.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.IsSuccess);
        if (segmentCount == 1)
        {
            Assert.Equal(
                GetExpectedEmbeddedAudioMode(needsAudio),
                test.Muxer?.MergeEmbeddedAudioMode);
            Assert.Null(test.Muxer?.ConcatEmbeddedAudioMode);
        }
        else
        {
            Assert.Equal(
                GetExpectedEmbeddedAudioMode(needsAudio),
                test.Muxer?.ConcatEmbeddedAudioMode);
            Assert.Null(test.Muxer?.MergeEmbeddedAudioMode);
        }
    }

    private static FfmpegEmbeddedAudioMode GetExpectedEmbeddedAudioMode(bool needsAudio) =>
        needsAudio
            ? FfmpegEmbeddedAudioMode.Required
            : FfmpegEmbeddedAudioMode.Excluded;

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task DurlWithSeparateAudioExcludesEmbeddedAudioAndMergesSelectedTrack(
        int segmentCount)
    {
        var requestedContent = DownloadContentSelection.None with
        {
            Audio = true,
            Video = true,
            MediaKind = DownloadMediaKind.DurlWithDashAudio
        };
        var test = await MuxTestContext.CreateAsync(requestedContent).ConfigureAwait(true);
        await using var testLifetime = test.ConfigureAwait(true);
        var sources = await test.AddDurlSourcesAsync(segmentCount).ConfigureAwait(true);
        test.Execution.MediaKind = DownloadMediaKind.DurlWithDashAudio;
        var stage = test.CreateStage(new FfmpegOperationResult(
            succeeded: true,
            outputPath: test.Execution.WorkingBasePath + ".mp4",
            failureReason: null,
            duration: TimeSpan.Zero));

        var result = await stage.ExecuteAsync(
            test.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.IsSuccess);
        if (segmentCount == 1)
        {
            Assert.Equal(test.AudioFile, test.Muxer!.MergeAudio);
            Assert.Equal(sources[0].FilePath, test.Muxer.MergeVideo);
            Assert.Equal(FfmpegEmbeddedAudioMode.Excluded,
                test.Muxer.MergeEmbeddedAudioMode);
            Assert.Equal(0, test.Muxer.ConcatCalls);
        }
        else
        {
            Assert.Equal(test.AudioFile, test.Muxer!.ConcatExternalAudio);
            Assert.Equal(test.Execution.WorkingBasePath + ".mp4",
                test.Muxer.ConcatOutput);
            Assert.Equal(FfmpegEmbeddedAudioMode.Excluded,
                test.Muxer.ConcatEmbeddedAudioMode);
            Assert.Equal(1, test.Muxer.ConcatCalls);
            Assert.Null(test.Muxer.MergeAudio);
            Assert.False(File.Exists(test.Execution.WorkingBasePath + ".durl-video.mp4"));
        }

        Assert.All(sources, source => Assert.True(File.Exists(source.FilePath)));
    }

    [Fact]
    public async Task InvalidAudioRevokesOnlyAudioCacheAndKeepsValidVideo()
    {
        var test = await MuxTestContext.CreateAsync().ConfigureAwait(true);
        await using var testLifetime = test.ConfigureAwait(true);
        var stage = test.CreateStage(ConfirmedInvalidInputs(
            "audio decode failed",
            test.AudioFile));

        var result = await stage.ExecuteAsync(
            test.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.mux.invalid-source", result.Error?.Code);
        Assert.False(File.Exists(test.AudioFile));
        Assert.False(File.Exists($"{test.AudioFile}.aria2"));
        Assert.False(File.Exists($"{test.AudioFile}.download"));
        Assert.True(File.Exists(test.VideoFile));
        var task = await test.GetTaskAsync().ConfigureAwait(true);
        Assert.DoesNotContain(test.AudioKey, task.Transfer.CompletedFileKeys);
        Assert.Contains(test.VideoKey, task.Transfer.CompletedFileKeys);
        Assert.Null(task.Transfer.BackendIdentity);
    }

    [Fact]
    public async Task InfrastructureFailurePreservesCompletedSourcesAndResumeIdentity()
    {
        var test = await MuxTestContext.CreateAsync().ConfigureAwait(true);
        await using var testLifetime = test.ConfigureAwait(true);
        var stage = test.CreateStage(FfmpegOperationResult.Failure(
            "ffmpeg unavailable"));

        var result = await stage.ExecuteAsync(
            test.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.mux.dash", result.Error?.Code);
        Assert.True(File.Exists(test.AudioFile));
        Assert.True(File.Exists(test.VideoFile));
        var task = await test.GetTaskAsync().ConfigureAwait(true);
        Assert.Contains(test.AudioKey, task.Transfer.CompletedFileKeys);
        Assert.Contains(test.VideoKey, task.Transfer.CompletedFileKeys);
        Assert.Equal("resume-identity", task.Transfer.BackendIdentity);
    }

    [Fact]
    public async Task DurlFailureRevokesOnlyDiagnosedCorruptSegment()
    {
        var test = await MuxTestContext.CreateAsync().ConfigureAwait(true);
        await using var testLifetime = test.ConfigureAwait(true);
        DurlTestSource[] sources = await test.AddDurlSourcesAsync().ConfigureAwait(true);
        var corrupt = sources[1];
        var stage = test.CreateStage(ConfirmedInvalidInputs(
            "segment decode failed",
            corrupt.FilePath));

        var result = await stage.ExecuteAsync(
            test.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.mux.invalid-source", result.Error?.Code);
        Assert.True(File.Exists(sources[0].FilePath));
        Assert.False(File.Exists(corrupt.FilePath));
        Assert.True(File.Exists(sources[2].FilePath));
        var task = await test.GetTaskAsync().ConfigureAwait(true);
        Assert.Contains(sources[0].TransferKey, task.Transfer.CompletedFileKeys);
        Assert.DoesNotContain(corrupt.TransferKey, task.Transfer.CompletedFileKeys);
        Assert.Contains(sources[2].TransferKey, task.Transfer.CompletedFileKeys);
    }

    [Fact]
    public async Task CleanupFailurePreservesDurableCacheAndRemainingSidecars()
    {
        var test = await MuxTestContext.CreateAsync().ConfigureAwait(true);
        await using var testLifetime = test.ConfigureAwait(true);
        File.Delete(test.AudioFile);
        Directory.CreateDirectory(test.AudioFile);
        var stage = test.CreateStage(ConfirmedInvalidInputs(
            "audio decode failed",
            test.AudioFile));

        var result = await stage.ExecuteAsync(
            test.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.mux.invalid-source-cleanup", result.Error?.Code);
        Assert.True(Directory.Exists(test.AudioFile));
        Assert.True(File.Exists($"{test.AudioFile}.aria2"));
        Assert.True(File.Exists($"{test.AudioFile}.download"));
        Assert.True(File.Exists(test.VideoFile));
        var task = await test.GetTaskAsync().ConfigureAwait(true);
        Assert.Contains(test.AudioKey, task.Transfer.CompletedFileKeys);
        Assert.Contains(test.VideoKey, task.Transfer.CompletedFileKeys);
        Assert.Equal("resume-identity", task.Transfer.BackendIdentity);
    }

    [Fact]
    public async Task MixedCleanupOutcomesInvalidateOnlySuccessfullyRemovedSources()
    {
        var test = await MuxTestContext.CreateAsync().ConfigureAwait(true);
        await using var testLifetime = test.ConfigureAwait(true);
        var sources = await test.AddDurlSourcesAsync().ConfigureAwait(true);
        File.Delete(sources[1].FilePath);
        Directory.CreateDirectory(sources[1].FilePath);
        var stage = test.CreateStage(ConfirmedInvalidInputs(
            "multiple segment decode failures",
            sources[0].FilePath,
            sources[1].FilePath));

        var result = await stage.ExecuteAsync(
            test.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.mux.invalid-source-cleanup", result.Error?.Code);
        Assert.False(File.Exists(sources[0].FilePath));
        Assert.True(Directory.Exists(sources[1].FilePath));
        Assert.True(File.Exists(sources[2].FilePath));
        var task = await test.GetTaskAsync().ConfigureAwait(true);
        Assert.DoesNotContain(sources[0].TransferKey, task.Transfer.CompletedFileKeys);
        Assert.Contains(sources[1].TransferKey, task.Transfer.CompletedFileKeys);
        Assert.Contains(sources[2].TransferKey, task.Transfer.CompletedFileKeys);
    }

    private static FfmpegOperationResult ConfirmedInvalidInputs(
        string reason,
        params string[] paths)
    {
        return FfmpegOperationResult.Failure(
            reason,
            FfmpegOperationFailureKind.InvalidInput,
            paths.Select(path => new FfmpegInputFailure(
                path,
                FfmpegInputFailureKind.DecodeCorruption)).ToArray());
    }

    private sealed class MuxTestContext : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly TestSettingsStore _settings;
        private readonly DownloadTaskApplicationService _tasks;
        private readonly DownloadTaskProjectionStore _projectionStore;
        private readonly DownloadTaskStateWriter _stateWriter;

        private MuxTestContext(
            string directory,
            TestSettingsStore settings,
            DownloadTaskApplicationService tasks,
            DownloadTaskProjectionStore projectionStore,
            DownloadTaskStateWriter stateWriter,
            DownloadExecutionContext execution,
            string audioFile,
            string videoFile,
            string audioKey,
            string videoKey)
        {
            _directory = directory;
            _settings = settings;
            _tasks = tasks;
            _projectionStore = projectionStore;
            _stateWriter = stateWriter;
            Execution = execution;
            AudioFile = audioFile;
            VideoFile = videoFile;
            AudioKey = audioKey;
            VideoKey = videoKey;
        }

        public DownloadExecutionContext Execution { get; }

        public string AudioFile { get; }

        public string VideoFile { get; }

        public string AudioKey { get; }

        public string VideoKey { get; }

        public StubMuxer? Muxer { get; private set; }

        public static async Task<MuxTestContext> CreateAsync(
            DownloadContentSelection? requestedContent = null)
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                $"downkyi-mux-recovery-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var settings = new TestSettingsStore();
            var store = new SingleTaskStore();
            var clock = new SystemClock();
            var historyService = DownloadHistoryService.CreateForSharedStore(store);
            var tasks = new DownloadTaskApplicationService(store, historyService, clock);
            var projectionStore = new DownloadTaskProjectionStore(
                tasks,
                historyService,
                clock);
            var stateWriter = new DownloadTaskStateWriter(tasks);
            var taskId = new DownloadTaskId("mux-recovery");
            var downloadBase = new DownloadBase
            {
                Id = taskId.Value,
                FilePath = Path.Combine(directory, "output"),
                NeedDownloadContent = requestedContent ?? DownloadContentSelection.All with
                {
                    MediaKind = DownloadMediaKind.Dash
                }
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
            await projectionStore.AddDownloadingAsync(
                downloading,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            await stateWriter.StartAsync(taskId, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            const string audioKey = "audio-key";
            const string videoKey = "video-key";
            var audioFile = Path.Combine(directory, "audio.m4s");
            var videoFile = Path.Combine(directory, "video.m4s");
            foreach (var path in new[]
                     {
                         audioFile,
                         $"{audioFile}.aria2",
                         $"{audioFile}.download",
                         videoFile
                     })
            {
                await File.WriteAllBytesAsync(
                    path,
                    [1, 2, 3],
                    TestContext.Current.CancellationToken).ConfigureAwait(true);
            }

            await stateWriter.RecordTransferFileAsync(
                taskId,
                audioKey,
                Path.GetFileName(audioFile),
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            await stateWriter.CompleteTransferFileAsync(
                taskId,
                audioKey,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            await stateWriter.RecordTransferFileAsync(
                taskId,
                videoKey,
                Path.GetFileName(videoFile),
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            await stateWriter.CompleteTransferFileAsync(
                taskId,
                videoKey,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            await stateWriter.SetBackendIdentityAsync(
                taskId,
                "resume-identity",
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            var execution = new DownloadExecutionContextFactory(
                projectionStore,
                settings.Store).Create(taskId);
            execution.StagingDirectory = directory;
            execution.MediaKind = DownloadMediaKind.Dash;
            execution.AudioFile = audioFile;
            execution.AudioTransferKey = audioKey;
            execution.VideoFile = videoFile;
            execution.VideoTransferKey = videoKey;
            return new MuxTestContext(
                directory,
                settings,
                tasks,
                projectionStore,
                stateWriter,
                execution,
                audioFile,
                videoFile,
                audioKey,
                videoKey);
        }

        public MuxStage CreateStage(FfmpegOperationResult result)
        {
            Muxer = new StubMuxer(result);
            return new MuxStage(
                new DownloadActivityPresenter(_projectionStore, _stateWriter),
                Muxer,
                _stateWriter,
                NullLogger<MuxStage>.Instance);
        }

        public async Task<DurlTestSource[]> AddDurlSourcesAsync(int count = 3)
        {
            var sources = Enumerable.Range(1, count)
                .Select(order => new DurlTestSource(
                    order,
                    $"durl-{order}-key",
                    Path.Combine(_directory, $"durl-{order}.flv")))
                .ToArray();
            foreach (var source in sources)
            {
                await File.WriteAllBytesAsync(
                    source.FilePath,
                    [1, 2, 3],
                    TestContext.Current.CancellationToken).ConfigureAwait(true);
                await _stateWriter.RecordTransferFileAsync(
                    Execution.TaskId,
                    source.TransferKey,
                    Path.GetFileName(source.FilePath),
                    TestContext.Current.CancellationToken).ConfigureAwait(true);
                await _stateWriter.CompleteTransferFileAsync(
                    Execution.TaskId,
                    source.TransferKey,
                    TestContext.Current.CancellationToken).ConfigureAwait(true);
            }

            Execution.MediaKind = DownloadMediaKind.Durl;
            Execution.DurlDownloads = sources
                .Select(source => new DurlDownloadResult(
                    new PlayUrlDurl
                    {
                        Order = source.Order,
                        Length = 5_000
                    },
                    source.FilePath,
                    source.TransferKey))
                .ToArray();
            return sources;
        }

        public async Task<DownloadTask> GetTaskAsync()
        {
            return await _tasks.FindAsync(
                       Execution.TaskId,
                       TestContext.Current.CancellationToken).ConfigureAwait(true)
                   ?? throw new InvalidOperationException("Test task disappeared.");
        }

        public ValueTask DisposeAsync()
        {
            _projectionStore.Dispose();
            _tasks.Dispose();
            _settings.Dispose();
            Directory.Delete(_directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubMuxer(FfmpegOperationResult result) : IFfmpegMediaMuxer
    {
        public FfmpegEmbeddedAudioMode? ConcatEmbeddedAudioMode { get; private set; }

        public FfmpegEmbeddedAudioMode? MergeEmbeddedAudioMode { get; private set; }

        public string? MergeAudio { get; private set; }

        public string? MergeVideo { get; private set; }

        public string? ConcatOutput { get; private set; }

        public string? ConcatExternalAudio { get; private set; }

        public int ConcatCalls { get; private set; }

        public Task<FfmpegOperationResult> ConcatDurlVideosAsync(
            VideoApplicationSettings videoSettings,
            IReadOnlyList<FfmpegConcatSegment> segments,
            string outputVideo,
            bool overwriteDestination,
            Action<string>? action = null,
            FfmpegEmbeddedAudioMode embeddedAudioMode = FfmpegEmbeddedAudioMode.Optional,
            string? externalAudio = null,
            CancellationToken cancellationToken = default)
        {
            ConcatEmbeddedAudioMode = embeddedAudioMode;
            ConcatOutput = outputVideo;
            ConcatExternalAudio = externalAudio;
            ConcatCalls++;
            if (result.Succeeded)
            {
                File.WriteAllBytes(outputVideo, [1, 2, 3]);
            }
            return Task.FromResult(result);
        }

        public Task<FfmpegOperationResult> MergeMediaAsync(
            VideoApplicationSettings videoSettings,
            string? audio,
            string? video,
            string destination,
            bool overwriteDestination,
            FfmpegEmbeddedAudioMode embeddedAudioMode = FfmpegEmbeddedAudioMode.Optional,
            CancellationToken cancellationToken = default)
        {
            MergeEmbeddedAudioMode = embeddedAudioMode;
            MergeAudio = audio;
            MergeVideo = video;
            if (result.Succeeded)
            {
                File.WriteAllBytes(destination, [1, 2, 3]);
            }
            return Task.FromResult(result);
        }
    }

    private sealed record DurlTestSource(int Order, string TransferKey, string FilePath);

    private sealed class SingleTaskStore :
        IDownloadTaskStore,
        IDownloadHistoryStore,
        IDownloadCompletionStore
    {
        private DownloadTask? _task;

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<OperationResult> AddAsync(
            DownloadTask task,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _task = task;
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> AddHistoryAsync(
            DownloadHistoryRecord history,
            CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Success());

        public Task<OperationResult> UpdateAsync(
            DownloadTask task,
            long expectedVersion,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_task?.Version != expectedVersion)
            {
                return Task.FromResult(OperationResult.Failure(
                    new OperationError(
                        "test.version",
                        "Unexpected task version.",
                        OperationErrorKind.Conflict)));
            }

            _task = task;
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> CompleteAsync(
            DownloadTask task,
            DownloadHistoryRecord history,
            long expectedVersion,
            CancellationToken cancellationToken)
        {
            _task = null;
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
            return Task.FromResult(_task?.Id == taskId ? _task : null);
        }

        public Task<IReadOnlyList<DownloadTask>> GetUnfinishedAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DownloadTask>>(_task == null ? [] : [_task]);

        public Task<bool> IsOutputPathReservedAsync(
            string basePath,
            bool ignoreCase,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<IReadOnlyList<string>> GetActiveOutputReservationKeysAsync(
            bool ignoreCase,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<DownloadHistoryPage> GetHistoryPageAsync(
            DownloadHistoryCursor? cursor,
            int pageSize,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DownloadHistoryPage([], null));

        public Task<OperationResult> DeleteAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Success());

        public Task<OperationResult> DeleteHistoryAsync(
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
