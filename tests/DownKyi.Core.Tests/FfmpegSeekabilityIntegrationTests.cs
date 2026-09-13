using DownKyi.Core.FFmpeg;
using DownKyi.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Core.Tests;

public sealed class FfmpegSeekabilityIntegrationTests : IDisposable
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(2);
    private readonly string _testDirectory = Path.Combine(
        Path.GetTempPath(),
        $"downkyi-ffmpeg-integration-{Guid.NewGuid():N}");

    public FfmpegSeekabilityIntegrationTests()
    {
        Directory.CreateDirectory(_testDirectory);
    }

    [Fact]
    [Trait("Category", "FfmpegIntegration")]
    public async Task MultiSegmentDurlOutputDecodesAfterMiddleAndTailSeek()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var processRunner = new FfmpegProcessRunner();
        if (!await IsToolAvailableAsync(
                processRunner,
                FfmpegExecutableLocator.Ffmpeg,
                cancellationToken).ConfigureAwait(true) ||
            !await IsToolAvailableAsync(
                processRunner,
                FfmpegExecutableLocator.Ffprobe,
                cancellationToken).ConfigureAwait(true))
        {
            Assert.Skip("ffmpeg and ffprobe are required for the seekability integration test.");
        }

        var first = Path.Combine(_testDirectory, "segment-1.mp4");
        var second = Path.Combine(_testDirectory, "segment-2.mp4");
        await CreateSegmentAsync(processRunner, first, "red", cancellationToken).ConfigureAwait(true);
        await CreateSegmentAsync(processRunner, second, "blue", cancellationToken).ConfigureAwait(true);

        var runtime = new FfmpegConcatRuntime(
            processRunner,
            new FfmpegMediaValidator(processRunner),
            new AsyncConcurrencyGate(() => 1),
            NullLogger<FfmpegConcatRuntime>.Instance);
        var output = Path.Combine(_testDirectory, "seekable.mp4");

        var result = await runtime.ConcatAsync(
            [
                new FfmpegConcatSegment(2, second, TimeSpan.FromSeconds(2)),
                new FfmpegConcatSegment(1, first, TimeSpan.FromSeconds(2))
            ],
            output,
            hardwareEncoder: null,
            allowStreamCopy: false,
            overwriteDestination: true,
            cancellationToken: cancellationToken).ConfigureAwait(true);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.True(File.Exists(output));
        Assert.InRange(result.Duration.TotalSeconds, 3.8, 4.2);
    }

    [Fact]
    [Trait("Category", "FfmpegIntegration")]
    public async Task FailedRealMuxIdentifiesOnlyUndecodableSource()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var processRunner = new FfmpegProcessRunner();
        if (!await IsToolAvailableAsync(
                processRunner,
                FfmpegExecutableLocator.Ffmpeg,
                cancellationToken).ConfigureAwait(true))
        {
            Assert.Skip("ffmpeg is required for the mux source-diagnostic integration test.");
        }

        var invalidAudio = Path.Combine(_testDirectory, "invalid-audio.m4s");
        await File.WriteAllBytesAsync(
            invalidAudio,
            [1, 2, 3],
            cancellationToken).ConfigureAwait(true);
        var validVideo = Path.Combine(_testDirectory, "valid-video.mp4");
        await CreateSegmentAsync(
            processRunner,
            validVideo,
            "green",
            cancellationToken).ConfigureAwait(true);
        using var settings = new SettingsStore(Path.Combine(_testDirectory, "settings.json"));
        var processor = new FfmpegProcessor(
            settings,
            NullLoggerFactory.Instance,
            processRunner);

        var result = await processor.MergeMediaAsync(
            settings.Current.Video,
            invalidAudio,
            validVideo,
            Path.Combine(_testDirectory, "invalid-output.mp4"),
            overwriteDestination: false,
            cancellationToken).ConfigureAwait(true);

        Assert.False(result.Succeeded);
        Assert.Equal(invalidAudio, Assert.Single(result.InvalidInputPaths));
        Assert.True(File.Exists(invalidAudio));
        Assert.True(File.Exists(validVideo));
    }

    public void Dispose()
    {
        Directory.Delete(_testDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static async Task<bool> IsToolAvailableAsync(
        FfmpegProcessRunner processRunner,
        string executable,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            new FfmpegCommand(executable, ["-version"], "version-check"),
            TimeSpan.FromSeconds(10),
            cancellationToken).ConfigureAwait(false);
        return result.Succeeded;
    }

    private static async Task CreateSegmentAsync(
        FfmpegProcessRunner processRunner,
        string output,
        string color,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            new FfmpegCommand(
                FfmpegExecutableLocator.Ffmpeg,
                [
                    "-hide_banner",
                    "-nostdin",
                    "-y",
                    "-f", "lavfi",
                    "-i", $"color=c={color}:s=320x240:r=30:d=2",
                    "-c:v", "libx264",
                    "-pix_fmt", "yuv420p",
                    output
                ],
                "create-test-segment"),
            ProcessTimeout,
            cancellationToken).ConfigureAwait(false);

        Assert.True(result.Succeeded, result.StandardError);
    }
}
