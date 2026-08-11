using DownKyi.Core.FFmpeg;
using DownKyi.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Core.Tests;

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
    public async Task MergeFailureReportsOnlySourceThatFailsDecodeValidation()
    {
        var audio = CreateInput("audio.m4s");
        var video = CreateInput("video.m4s");
        var runner = new MergeFailureRunner(audio, DiagnosticFailure.DecodeCorruption);
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
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(FfmpegOperationFailureKind.InvalidInput, result.FailureKind);
        Assert.Equal(audio, Assert.Single(result.InvalidInputPaths));
        Assert.Equal(
            FfmpegInputFailureKind.DecodeCorruption,
            Assert.Single(result.InputFailures, failure => failure.Path == audio).Kind);
        Assert.Equal(2, runner.InputValidationCount);
        Assert.True(File.Exists(audio));
        Assert.True(File.Exists(video));
    }

    [Fact]
    public async Task MergeInfrastructureFailureDoesNotAccuseOrDeleteInputs()
    {
        var audio = CreateInput("audio.m4s");
        var video = CreateInput("video.m4s");
        var processor = new FfmpegProcessor(
            _settings,
            NullLoggerFactory.Instance,
            new MergeFailureRunner(audio, DiagnosticFailure.ProcessNotStarted));

        var result = await processor.MergeMediaAsync(
            _settings.Current.Video,
            audio,
            video,
            Path.Combine(_directory, "output.mp4"),
            overwriteDestination: false,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(FfmpegOperationFailureKind.ProcessUnavailable, result.FailureKind);
        Assert.Empty(result.InvalidInputPaths);
        Assert.True(File.Exists(audio));
        Assert.True(File.Exists(video));
    }

    [Fact]
    public async Task StartedValidationInfrastructureFailureDoesNotAccuseInputs()
    {
        var audio = CreateInput("started-infrastructure-audio.m4s");
        var video = CreateInput("started-infrastructure-video.m4s");
        var processor = new FfmpegProcessor(
            _settings,
            NullLoggerFactory.Instance,
            new MergeFailureRunner(audio, DiagnosticFailure.StartedInfrastructure));

        var result = await processor.MergeMediaAsync(
            _settings.Current.Video,
            audio,
            video,
            Path.Combine(_directory, "started-infrastructure-output.mp4"),
            overwriteDestination: false,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(FfmpegOperationFailureKind.ProcessFailure, result.FailureKind);
        Assert.Empty(result.InvalidInputPaths);
        Assert.All(
            result.InputFailures,
            failure => Assert.Equal(FfmpegInputFailureKind.DiagnosticFailure, failure.Kind));
        Assert.True(File.Exists(audio));
        Assert.True(File.Exists(video));
    }

    [Fact]
    public async Task InputDirectoryIsReportedAsAccessFailureWithoutInvalidation()
    {
        var audio = Path.Combine(_directory, "directory-input.m4s");
        Directory.CreateDirectory(audio);
        var video = CreateInput("directory-input-video.m4s");
        var runner = new SuccessfulMergeRunner();
        var processor = new FfmpegProcessor(
            _settings,
            NullLoggerFactory.Instance,
            runner);

        var result = await processor.MergeMediaAsync(
            _settings.Current.Video,
            audio,
            video,
            Path.Combine(_directory, "directory-input-output.mp4"),
            overwriteDestination: false,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(FfmpegOperationFailureKind.InputAccess, result.FailureKind);
        Assert.Empty(result.InvalidInputPaths);
        Assert.Equal(
            FfmpegInputFailureKind.UnsupportedFileType,
            Assert.Single(result.InputFailures).Kind);
        Assert.Equal(0, runner.CallCount);
        Assert.True(Directory.Exists(audio));
        Assert.True(File.Exists(video));
    }

    [Fact]
    public async Task DestinationConflictDoesNotTriggerInputDiagnosticsOrDeleteSources()
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
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(FfmpegOperationFailureKind.DestinationConflict, result.FailureKind);
        Assert.Empty(result.InputFailures);
        Assert.Empty(result.InvalidInputPaths);
        Assert.Equal(1, runner.CallCount);
        Assert.True(File.Exists(audio));
        Assert.True(File.Exists(video));
        Assert.Equal(
            originalDestination,
            await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MergeDiagnosticsShareConfiguredFfmpegConcurrencyBudget()
    {
        _settings.Update(settings => settings with
        {
            Video = settings.Video with { FfmpegMaxParallelJobs = 1 }
        });
        var firstAudio = CreateInput("concurrency-first-audio.m4s");
        var firstVideo = CreateInput("concurrency-first-video.m4s");
        var secondAudio = CreateInput("concurrency-second-audio.m4s");
        var secondVideo = CreateInput("concurrency-second-video.m4s");
        var runner = new BlockingDiagnosticRunner();
        var processor = new FfmpegProcessor(
            _settings,
            NullLoggerFactory.Instance,
            runner);

        var first = processor.MergeMediaAsync(
            _settings.Current.Video,
            firstAudio,
            firstVideo,
            Path.Combine(_directory, "concurrency-first.mp4"),
            overwriteDestination: false,
            TestContext.Current.CancellationToken);
        await runner.ValidationStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        var second = processor.MergeMediaAsync(
            _settings.Current.Video,
            secondAudio,
            secondVideo,
            Path.Combine(_directory, "concurrency-second.mp4"),
            overwriteDestination: false,
            TestContext.Current.CancellationToken);

        await Task.WhenAny(
            runner.ConcurrentCallObserved.Task,
            Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken));
        runner.ReleaseValidation.TrySetResult();
        await Task.WhenAll(first, second);

        Assert.False(runner.ConcurrentCallObserved.Task.IsCompletedSuccessfully);
        Assert.Equal(1, runner.MaximumActiveCalls);
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

    private sealed class MergeFailureRunner(
        string invalidInput,
        DiagnosticFailure diagnosticFailure) : IFfmpegProcessRunner
    {
        public int InputValidationCount { get; private set; }

        public Task<FfmpegProcessResult> RunAsync(
            FfmpegCommand command,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (command.Operation == "merge-media")
            {
                return Task.FromResult(Failure());
            }

            Assert.Equal("validate-input", command.Operation);
            InputValidationCount++;
            var inputIndex = command.Arguments.ToList().IndexOf("-i");
            var input = command.Arguments[inputIndex + 1];
            return Task.FromResult(
                diagnosticFailure == DiagnosticFailure.DecodeCorruption && string.Equals(
                    input,
                    invalidInput,
                    StringComparison.Ordinal)
                    ? new FfmpegProcessResult(
                        false,
                        1,
                        string.Empty,
                        "Error while decoding stream #0:0: Invalid data found when processing input",
                        false)
                    : diagnosticFailure != DiagnosticFailure.DecodeCorruption
                        ? Failure()
                        : new FfmpegProcessResult(true, 0, string.Empty, string.Empty, false));
        }

        private FfmpegProcessResult Failure() => diagnosticFailure switch
        {
            DiagnosticFailure.ProcessNotStarted => new FfmpegProcessResult(
                false,
                -1,
                string.Empty,
                "ffmpeg unavailable",
                false,
                ProcessStarted: false),
            DiagnosticFailure.StartedInfrastructure => new FfmpegProcessResult(
                false,
                1,
                string.Empty,
                "Permission denied while loading runtime dependency.",
                false),
            _ => new FfmpegProcessResult(false, 1, string.Empty, "mux failed", false)
        };
    }

    private sealed class BlockingDiagnosticRunner : IFfmpegProcessRunner
    {
        private int _activeCalls;
        private int _maximumActiveCalls;

        public TaskCompletionSource ValidationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseValidation { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ConcurrentCallObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int MaximumActiveCalls => Volatile.Read(ref _maximumActiveCalls);

        public async Task<FfmpegProcessResult> RunAsync(
            FfmpegCommand command,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var active = Interlocked.Increment(ref _activeCalls);
            UpdateMaximum(active);
            if (active > 1)
            {
                ConcurrentCallObserved.TrySetResult();
            }

            try
            {
                if (command.Operation == "merge-media")
                {
                    return new FfmpegProcessResult(false, 1, string.Empty, "mux failed", false);
                }

                Assert.Equal("validate-input", command.Operation);
                ValidationStarted.TrySetResult();
                await ReleaseValidation.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return new FfmpegProcessResult(true, 0, string.Empty, string.Empty, false);
            }
            finally
            {
                Interlocked.Decrement(ref _activeCalls);
            }
        }

        private void UpdateMaximum(int active)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maximumActiveCalls);
                if (current >= active ||
                    Interlocked.CompareExchange(ref _maximumActiveCalls, active, current) == current)
                {
                    return;
                }
            }
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

    private enum DiagnosticFailure
    {
        DecodeCorruption,
        ProcessNotStarted,
        StartedInfrastructure
    }
}
