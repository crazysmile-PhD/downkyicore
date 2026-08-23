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
        private readonly DownloadTaskStateWriter _stateWriter;

        private MuxTestContext(
            string directory,
            TestSettingsStore settings,
            DownloadTaskApplicationService tasks,
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

        public static async Task<MuxTestContext> CreateAsync()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                $"downkyi-mux-recovery-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var settings = new TestSettingsStore();
            var store = new SingleTaskStore();
            var tasks = new DownloadTaskApplicationService(store, new SystemClock());
            var stateWriter = new DownloadTaskStateWriter(tasks);
            var taskId = new DownloadTaskId("mux-recovery");
            var downloadBase = new DownloadBase
            {
                Id = taskId.Value,
                FilePath = Path.Combine(directory, "output")
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
            var task = DownloadTaskProjectionMapper.CreateNewTask(
                downloading,
                DateTimeOffset.UnixEpoch);
            Assert.True((await tasks.AddAsync(
                task,
                TestContext.Current.CancellationToken).ConfigureAwait(true)).IsSuccess);
            await stateWriter.StartAsync(taskId, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            downloading.Downloading.DownloadStatus = DownloadStatus.Downloading;

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

            var execution = new DownloadExecutionContext(
                taskId,
                downloading,
                settings.Store.Current,
                static (_, token) => token.ThrowIfCancellationRequested())
            {
                MediaKind = DownloadMediaKind.Dash,
                AudioFile = audioFile,
                AudioTransferKey = audioKey,
                VideoFile = videoFile,
                VideoTransferKey = videoKey
            };
            return new MuxTestContext(
                directory,
                settings,
                tasks,
                stateWriter,
                execution,
                audioFile,
                videoFile,
                audioKey,
                videoKey);
        }

        public MuxStage CreateStage(FfmpegOperationResult result)
        {
            return new MuxStage(
                new DownloadActivityPresenter(_stateWriter),
                new StubMuxer(result),
                _stateWriter,
                NullLogger<MuxStage>.Instance);
        }

        public async Task<DurlTestSource[]> AddDurlSourcesAsync()
        {
            var sources = Enumerable.Range(1, 3)
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
            _tasks.Dispose();
            _settings.Dispose();
            Directory.Delete(_directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubMuxer(FfmpegOperationResult result) : IFfmpegMediaMuxer
    {
        public Task<FfmpegOperationResult> ConcatDurlVideosAsync(
            VideoApplicationSettings videoSettings,
            IReadOnlyList<FfmpegConcatSegment> segments,
            string outputVideo,
            bool overwriteDestination,
            Action<string>? action = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(result);

        public Task<FfmpegOperationResult> MergeMediaAsync(
            VideoApplicationSettings videoSettings,
            string? audio,
            string? video,
            string destination,
            bool overwriteDestination,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }

    private sealed record DurlTestSource(int Order, string TransferKey, string FilePath);

    private sealed class SingleTaskStore : IDownloadTaskStore
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

        public Task<DownloadHistoryPage> GetHistoryPageAsync(
            DownloadHistoryCursor? cursor,
            int pageSize,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DownloadHistoryPage([], null));

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
