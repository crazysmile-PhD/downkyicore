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
        var runner = new MergeFailureRunner(audio, infrastructureFailure: false);
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
        Assert.Equal(audio, Assert.Single(result.InvalidInputPaths));
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
            new MergeFailureRunner(audio, infrastructureFailure: true));

        var result = await processor.MergeMediaAsync(
            _settings.Current.Video,
            audio,
            video,
            Path.Combine(_directory, "output.mp4"),
            overwriteDestination: false,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Empty(result.InvalidInputPaths);
        Assert.True(File.Exists(audio));
        Assert.True(File.Exists(video));
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
        bool infrastructureFailure) : IFfmpegProcessRunner
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
                !infrastructureFailure && string.Equals(
                    input,
                    invalidInput,
                    StringComparison.Ordinal)
                    ? new FfmpegProcessResult(false, 1, string.Empty, "decode failed", false)
                    : infrastructureFailure
                        ? Failure()
                        : new FfmpegProcessResult(true, 0, string.Empty, string.Empty, false));
        }

        private FfmpegProcessResult Failure() => infrastructureFailure
            ? new FfmpegProcessResult(
                false,
                -1,
                string.Empty,
                "ffmpeg unavailable",
                false,
                ProcessStarted: false)
            : new FfmpegProcessResult(false, 1, string.Empty, "mux failed", false);
    }
}
