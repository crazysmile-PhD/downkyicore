using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
using DownKyi.Infrastructure.Time;
using DownKyi.Models;
using DownKyi.Services.Download;
using DownKyi.ViewModels.DownloadManager;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class DownloadArtifactStageTests
{
    [Fact]
    public async Task CoverHttpFailureStopsBeforeFinalize()
    {
        var client = new TestBilibiliApiClient
        {
            DownloadFileAsyncHandler = (_, _, _) =>
                Task.FromException(new HttpRequestException("unavailable"))
        };
        using var context = await ArtifactTestContext.CreateAsync(client, cover: true)
            .ConfigureAwait(true);
        context.Downloading.DownloadBase.CoverUrl = "https://example.test/cover.jpg";
        var finalized = false;

        var run = await DownloadPipeline.ExecuteStagesAsync(
            [context.Stage, new CallbackStage(() => finalized = true)],
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(run.Result.IsSuccess);
        Assert.Equal("download.artifact.cover.http", run.Result.Error?.Code);
        Assert.False(finalized);
    }

    [Fact]
    public async Task ZeroByteCoverFailsArtifactStage()
    {
        var client = new TestBilibiliApiClient
        {
            DownloadFileAsyncHandler = (_, destination, _) =>
            {
                using var output = File.Create(destination);
                return Task.CompletedTask;
            }
        };
        using var context = await ArtifactTestContext.CreateAsync(client, cover: true)
            .ConfigureAwait(true);
        context.Downloading.DownloadBase.CoverUrl = "https://example.test/cover.jpg";

        var result = await context.Stage.ExecuteAsync(
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.artifact.cover.invalid", result.Error?.Code);
    }

    [Fact]
    public async Task MissingSubtitleResourceIsAnExplicitSuccessfulSkip()
    {
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (_, _) => Task.FromResult(
                """
                {"code":0,"data":{"aid":1,"bvid":"BV1test","cid":2,"subtitle":{"subtitles":[]}}}
                """)
        };
        using var context = await ArtifactTestContext.CreateAsync(client, subtitle: true)
            .ConfigureAwait(true);

        var result = await context.Stage.ExecuteAsync(
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.IsSuccess);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<string>>(context.Execution.SubtitleFiles));
    }

    [Fact]
    public async Task MalformedSubtitleIsNotReportedAsNoResource()
    {
        var request = 0;
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (_, _) => Task.FromResult(request++ == 0
                ? """
                  {"code":0,"data":{"aid":1,"bvid":"BV1test","cid":2,"subtitle":{"subtitles":[{"lan":"ai-zh","lan_doc":"AI","subtitle_url":"//example.test/subtitle.json","type":1}]}}}
                  """
                : "{not-json")
        };
        using var context = await ArtifactTestContext.CreateAsync(client, subtitle: true)
            .ConfigureAwait(true);

        var result = await context.Stage.ExecuteAsync(
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.artifact.subtitle.parse", result.Error?.Code);
    }

    [Fact]
    public async Task SubtitleWriteFailureIsNotReportedAsNoResource()
    {
        var request = 0;
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (_, _) => Task.FromResult(request++ == 0
                ? """
                  {"code":0,"data":{"aid":1,"bvid":"BV1test","cid":2,"subtitle":{"subtitles":[{"lan":"zh","lan_doc":"Chinese","subtitle_url":"//example.test/subtitle.json","type":0}]}}}
                  """
                : """
                  {"body":[{"from":0,"to":1,"location":2,"content":"hello"}]}
                  """)
        };
        using var context = await ArtifactTestContext.CreateAsync(
            client,
            subtitle: true,
            useMissingOutputDirectory: true).ConfigureAwait(true);

        var result = await context.Stage.ExecuteAsync(
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.artifact.subtitle.io", result.Error?.Code);
    }

    [Fact]
    public async Task DanmakuHttpFailureFailsArtifactStage()
    {
        var client = new TestBilibiliApiClient
        {
            OpenReadAsyncHandler = (_, _) =>
                Task.FromException<Stream>(new HttpRequestException("unavailable"))
        };
        using var context = await ArtifactTestContext.CreateAsync(client, danmaku: true)
            .ConfigureAwait(true);

        var result = await context.Stage.ExecuteAsync(
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.artifact.danmaku.http", result.Error?.Code);
    }

    [Fact]
    public async Task MalformedDanmakuFailsArtifactStage()
    {
        var client = new TestBilibiliApiClient
        {
            OpenReadAsyncHandler = (_, _) =>
                Task.FromResult<Stream>(new MemoryStream([0x0A, 0x05, 0x01]))
        };
        using var context = await ArtifactTestContext.CreateAsync(client, danmaku: true)
            .ConfigureAwait(true);

        var result = await context.Stage.ExecuteAsync(
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.artifact.danmaku.parse", result.Error?.Code);
    }

    [Fact]
    public async Task ArtifactCancellationPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new TestBilibiliApiClient
        {
            DownloadFileAsyncHandler = (_, _, token) =>
                Task.FromException(new OperationCanceledException(token))
        };
        using var context = await ArtifactTestContext.CreateAsync(client, cover: true)
            .ConfigureAwait(true);
        context.Downloading.DownloadBase.CoverUrl = "https://example.test/cover.jpg";
        await cancellation.CancelAsync().ConfigureAwait(true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Stage.ExecuteAsync(context.Execution, cancellation.Token)).ConfigureAwait(true);
    }

    [Fact]
    public async Task SuccessfulArtifactStageAllowsFinalizeStage()
    {
        var client = new TestBilibiliApiClient
        {
            DownloadFileAsyncHandler = (_, destination, token) =>
                File.WriteAllTextAsync(destination, "image", token)
        };
        using var context = await ArtifactTestContext.CreateAsync(client, cover: true)
            .ConfigureAwait(true);
        context.Downloading.DownloadBase.CoverUrl = "https://example.test/cover.jpg";
        var finalized = false;

        var run = await DownloadPipeline.ExecuteStagesAsync(
            [context.Stage, new CallbackStage(() => finalized = true)],
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(run.Result.IsSuccess);
        Assert.True(finalized);
        Assert.True(File.Exists(context.Execution.CoverFile));
    }

    private sealed class CallbackStage(Action callback) : IDownloadPipelineStage
    {
        public string Name => "finalize";

        public Task<OperationResult<DownloadStageResult>> ExecuteAsync(
            DownloadExecutionContext context,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(context);
            cancellationToken.ThrowIfCancellationRequested();
            callback();
            return Task.FromResult(DownloadStageResult.Success(Name));
        }
    }

    private sealed class ArtifactTestContext : IDisposable
    {
        private readonly string _directory;
        private readonly DownKyi.Core.Settings.SettingsStore _settings;
        private readonly DownloadTaskApplicationService _tasks;

        private ArtifactTestContext(
            string directory,
            DownKyi.Core.Settings.SettingsStore settings,
            DownloadTaskApplicationService tasks,
            DownloadingItem downloading,
            DownloadExecutionContext execution,
            DownloadArtifactsStage stage)
        {
            _directory = directory;
            _settings = settings;
            _tasks = tasks;
            Downloading = downloading;
            Execution = execution;
            Stage = stage;
        }

        public DownloadingItem Downloading { get; }

        public DownloadExecutionContext Execution { get; }

        public DownloadArtifactsStage Stage { get; }

        public static async Task<ArtifactTestContext> CreateAsync(
            TestBilibiliApiClient client,
            bool cover = false,
            bool subtitle = false,
            bool danmaku = false,
            bool useMissingOutputDirectory = false)
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "downkyi-artifact-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var settings = new DownKyi.Core.Settings.SettingsStore(
                Path.Combine(directory, "settings.json"));
            var taskId = new DownloadTaskId(Guid.NewGuid().ToString("N"));
            var outputDirectory = useMissingOutputDirectory
                ? Path.Combine(directory, "missing")
                : directory;
            var downloadBase = new DownloadBase
            {
                Id = taskId.Value,
                Avid = 1,
                Bvid = "BV1test",
                Cid = 2,
                FilePath = Path.Combine(outputDirectory, "output")
            };
            foreach (var key in downloadBase.NeedDownloadContent.Keys.ToArray())
            {
                downloadBase.NeedDownloadContent[key] = false;
            }

            downloadBase.NeedDownloadContent["downloadCover"] = cover;
            downloadBase.NeedDownloadContent["downloadSubtitle"] = subtitle;
            downloadBase.NeedDownloadContent["downloadDanmaku"] = danmaku;
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
            var store = new SingleTaskStore();
            var tasks = new DownloadTaskApplicationService(store, new SystemClock());
            var stateWriter = new DownloadTaskStateWriter(tasks);
            var task = DownloadTaskProjectionMapper.CreateNewTask(
                downloading,
                DateTimeOffset.UnixEpoch);
            Assert.True((await tasks.AddAsync(
                task,
                TestContext.Current.CancellationToken).ConfigureAwait(true)).IsSuccess);
            await stateWriter.StartAsync(taskId, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            downloading.Downloading.DownloadStatus = DownloadStatus.Downloading;
            var writer = new DownloadArtifactWriter(
                new TestWbiKeyProvider(),
                stateWriter,
                NullLogger<DownloadArtifactWriter>.Instance,
                client);
            var execution = new DownloadExecutionContext(
                taskId,
                downloading,
                settings.Current,
                static (_, token) => token.ThrowIfCancellationRequested());
            return new ArtifactTestContext(
                directory,
                settings,
                tasks,
                downloading,
                execution,
                new DownloadArtifactsStage(writer));
        }

        public void Dispose()
        {
            _tasks.Dispose();
            _settings.Dispose();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }

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
            if (_task == null || _task.Version != expectedVersion)
            {
                return Task.FromResult(OperationResult.Failure(
                    OperationError.Unexpected("test.version", "Unexpected task version.")));
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
