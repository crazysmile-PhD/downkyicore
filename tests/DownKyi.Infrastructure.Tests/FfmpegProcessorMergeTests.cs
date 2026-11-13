using DownKyi.Application.Media;
using DownKyi.Infrastructure.FFmpeg;
using DownKyi.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Infrastructure.Tests;

public sealed class FfmpegProcessorMergeTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"downkyi-merge-{Guid.NewGuid():N}");
    private readonly SettingsStore _settings;

    public FfmpegProcessorMergeTests()
    {
        Directory.CreateDirectory(_directory);
        _settings = new SettingsStore(Path.Combine(_directory, "settings.json"));
    }

    [Fact]
    public async Task MergeProcessFailureDoesNotRunAdditionalDecodeOrDeleteInputs()
    {
        var audio = CreateInput("audio.m4s");
        var video = CreateInput("video.m4s");
        var runner = new MergeFailureRunner(processStarted: true);
        var processor = new FfmpegProcessor(
            _settings,
            NullLoggerFactory.Instance,
            runner);

        var result = await processor.MergeMediaAsync(
            _settings.Current.Video,
            audio,
            video,
            Path.Combine(_directory, "output.mp4"),
            overwriteDestination: false,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(FfmpegOperationFailureKind.ProcessFailure, result.FailureKind);
        Assert.Equal(1, runner.CallCount);
        Assert.True(File.Exists(audio));
        Assert.True(File.Exists(video));
    }

    [Fact]
    public async Task MergeUnavailableProcessPreservesFailureKindAndInputs()
    {
        var audio = CreateInput("unavailable-audio.m4s");
        var video = CreateInput("unavailable-video.m4s");
        var runner = new MergeFailureRunner(processStarted: false);
        var processor = new FfmpegProcessor(
            _settings,
            NullLoggerFactory.Instance,
            runner);

        var result = await processor.MergeMediaAsync(
            _settings.Current.Video,
            audio,
            video,
            Path.Combine(_directory, "unavailable-output.mp4"),
            overwriteDestination: false,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(FfmpegOperationFailureKind.ProcessUnavailable, result.FailureKind);
        Assert.Equal(1, runner.CallCount);
        Assert.True(File.Exists(audio));
        Assert.True(File.Exists(video));
    }

    [Fact]
    public async Task DestinationConflictDoesNotOverwriteDestinationOrDeleteInputs()
    {
        var audio = CreateInput("destination-audio.m4s");
        var video = CreateInput("destination-video.m4s");
        var destination = Path.Combine(_directory, "destination.mp4");
        byte[] originalDestination = [9, 8, 7];
        await File.WriteAllBytesAsync(
            destination,
            originalDestination,
            TestContext.Current.CancellationToken);
        var runner = new SuccessfulMergeRunner();
        var processor = new FfmpegProcessor(
            _settings,
            NullLoggerFactory.Instance,
            runner);

        var result = await processor.MergeMediaAsync(
            _settings.Current.Video,
            audio,
            video,
            destination,
            overwriteDestination: false,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(FfmpegOperationFailureKind.DestinationConflict, result.FailureKind);
        Assert.Equal(1, runner.CallCount);
        Assert.True(File.Exists(audio));
        Assert.True(File.Exists(video));
        Assert.Equal(
            originalDestination,
            await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InvalidCoverIsConfirmedWithoutStartingMediaMux()
    {
        var audio = CreateInput("cover-failure-audio.m4s");
        var video = CreateInput("cover-failure-video.m4s");
        var cover = CreateInput("cover-failure.webp");
        var runner = new CoverFailureRunner(
            "Invalid data found when processing input");
        var processor = new FfmpegProcessor(
            _settings,
            NullLoggerFactory.Instance,
            runner);

        var result = await processor.MergeMediaAsync(
            _settings.Current.Video,
            audio,
            video,
            Path.Combine(_directory, "cover-failure-output.mp4"),
            overwriteDestination: false,
            cover: cover,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(FfmpegOperationFailureKind.InputInvalid, result.FailureKind);
        Assert.Equal(FfmpegOperationFailureStage.Cover, result.FailureStage);
        Assert.Equal(["normalize-cover"], runner.Operations);
        Assert.True(File.Exists(audio));
        Assert.True(File.Exists(video));
        Assert.True(File.Exists(cover));
        AssertNoCoverTemporaryFiles();
    }

    [Fact]
    public async Task CoverOutputProcessFailureRemainsRetryable()
    {
        var audio = CreateInput("cover-output-failure-audio.m4s");
        var video = CreateInput("cover-output-failure-video.m4s");
        var cover = CreateInput("cover-output-failure.webp");
        var runner = new CoverFailureRunner("No space left on device");
        var processor = new FfmpegProcessor(
            _settings,
            NullLoggerFactory.Instance,
            runner);

        var result = await processor.MergeMediaAsync(
            _settings.Current.Video,
            audio,
            video,
            Path.Combine(_directory, "cover-output-failure.mp4"),
            overwriteDestination: false,
            cover: cover,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(FfmpegOperationFailureKind.ProcessFailure, result.FailureKind);
        Assert.Equal(FfmpegOperationFailureStage.Cover, result.FailureStage);
        Assert.Equal(["normalize-cover"], runner.Operations);
        Assert.True(File.Exists(audio));
        Assert.True(File.Exists(video));
        Assert.True(File.Exists(cover));
        AssertNoCoverTemporaryFiles();
    }

    [Fact]
    public async Task SuccessfulCoverNormalizationIsRemovedAfterTheSingleMediaMux()
    {
        var audio = CreateInput("cover-success-audio.m4s");
        var video = CreateInput("cover-success-video.m4s");
        var cover = CreateInput("cover-success.avif");
        var output = Path.Combine(_directory, "cover-success-output.mp4");
        var runner = new SuccessfulCoverMergeRunner();
        var processor = new FfmpegProcessor(
            _settings,
            NullLoggerFactory.Instance,
            runner);

        var result = await processor.MergeMediaAsync(
            _settings.Current.Video,
            audio,
            video,
            output,
            overwriteDestination: false,
            cover: cover,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(FfmpegOperationFailureStage.None, result.FailureStage);
        Assert.Equal(["normalize-cover", "merge-media"], runner.Operations);
        Assert.True(File.Exists(output));
        Assert.True(File.Exists(cover));
        AssertNoCoverTemporaryFiles();
    }

    [Fact]
    public async Task CoverNormalizationCancellationCleansTemporaryFilesBeforeReturning()
    {
        var audio = CreateInput("cover-cancel-audio.m4s");
        var video = CreateInput("cover-cancel-video.m4s");
        var cover = CreateInput("cover-cancel.gif");
        var runner = new CancelingCoverRunner();
        var processor = new FfmpegProcessor(
            _settings,
            NullLoggerFactory.Instance,
            runner);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processor.MergeMediaAsync(
            _settings.Current.Video,
            audio,
            video,
            Path.Combine(_directory, "cover-cancel-output.mp4"),
            overwriteDestination: false,
            cover: cover,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(["normalize-cover"], runner.Operations);
        AssertNoCoverTemporaryFiles();
    }

    public void Dispose()
    {
        _settings.Dispose();
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string CreateInput(string fileName)
    {
        var path = Path.Combine(_directory, fileName);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    private void AssertNoCoverTemporaryFiles()
    {
        Assert.DoesNotContain(
            Directory.EnumerateFiles(_directory),
            file => Path.GetFileName(file).Contains("downkyi-cover", StringComparison.Ordinal));
    }

    private sealed class MergeFailureRunner(bool processStarted) : IFfmpegProcessRunner
    {
        public int CallCount { get; private set; }

        public Task<FfmpegProcessResult> RunAsync(
            FfmpegCommand command,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            Assert.Equal("merge-media", command.Operation);
            return Task.FromResult(new FfmpegProcessResult(
                false,
                processStarted ? 1 : -1,
                string.Empty,
                "mux failed",
                false,
                ProcessStarted: processStarted));
        }
    }

    private sealed class SuccessfulMergeRunner : IFfmpegProcessRunner
    {
        public int CallCount { get; private set; }

        public async Task<FfmpegProcessResult> RunAsync(
            FfmpegCommand command,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            Assert.Equal("merge-media", command.Operation);
            await File.WriteAllBytesAsync(
                command.Arguments[^1],
                [1, 2, 3],
                cancellationToken).ConfigureAwait(false);
            return new FfmpegProcessResult(true, 0, string.Empty, string.Empty, false);
        }
    }

    private sealed class CoverFailureRunner(string standardError) : IFfmpegProcessRunner
    {
        public List<string> Operations { get; } = [];

        public Task<FfmpegProcessResult> RunAsync(
            FfmpegCommand command,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Operations.Add(command.Operation);
            return Task.FromResult(new FfmpegProcessResult(
                false,
                1,
                string.Empty,
                standardError,
                false));
        }
    }

    private sealed class SuccessfulCoverMergeRunner : IFfmpegProcessRunner
    {
        public List<string> Operations { get; } = [];

        public async Task<FfmpegProcessResult> RunAsync(
            FfmpegCommand command,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Operations.Add(command.Operation);
            await File.WriteAllBytesAsync(
                command.Arguments[^1],
                [1, 2, 3],
                cancellationToken).ConfigureAwait(false);
            return new FfmpegProcessResult(true, 0, string.Empty, string.Empty, false);
        }
    }

    private sealed class CancelingCoverRunner : IFfmpegProcessRunner
    {
        public List<string> Operations { get; } = [];

        public async Task<FfmpegProcessResult> RunAsync(
            FfmpegCommand command,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Operations.Add(command.Operation);
            await File.WriteAllBytesAsync(
                command.Arguments[^1],
                [1, 2, 3],
                CancellationToken.None).ConfigureAwait(false);
            throw new OperationCanceledException(cancellationToken);
        }
    }
}
