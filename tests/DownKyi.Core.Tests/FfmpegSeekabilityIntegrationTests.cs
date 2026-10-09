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
    public async Task MultiSegmentDurlWithIndependentAudioProducesOnePlayableOutput()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var processRunner = new FfmpegProcessRunner();
        if (!await IsToolAvailableAsync(
                processRunner, FfmpegExecutableLocator.Ffmpeg,
                cancellationToken).ConfigureAwait(true)
            || !await IsToolAvailableAsync(
                processRunner, FfmpegExecutableLocator.Ffprobe,
                cancellationToken).ConfigureAwait(true))
        {
            Assert.Skip("ffmpeg and ffprobe are required for the media integration test.");
        }

        var first = Path.Combine(_testDirectory, "cross-source-1.mp4");
        var second = Path.Combine(_testDirectory, "cross-source-2.mp4");
        var audio = Path.Combine(_testDirectory, "independent-audio.m4a");
        await CreateSegmentAsync(processRunner, first, "red", cancellationToken)
            .ConfigureAwait(true);
        await CreateSegmentAsync(processRunner, second, "blue", cancellationToken)
            .ConfigureAwait(true);
        await CreateAudioOnlyAsync(processRunner, audio, cancellationToken)
            .ConfigureAwait(true);
        var validator = new FfmpegMediaValidator(processRunner);
        var runtime = new FfmpegConcatRuntime(
            processRunner,
            validator,
            new AsyncConcurrencyGate(() => 1),
            NullLogger<FfmpegConcatRuntime>.Instance);
        var output = Path.Combine(_testDirectory, "cross-source.mp4");

        var result = await runtime.ConcatAsync(
            [
                new FfmpegConcatSegment(1, first, TimeSpan.FromSeconds(2)),
                new FfmpegConcatSegment(2, second, TimeSpan.FromSeconds(2))
            ],
            output,
            hardwareEncoder: null,
            allowStreamCopy: false,
            overwriteDestination: false,
            embeddedAudioMode: FfmpegEmbeddedAudioMode.Excluded,
            externalAudio: audio,
            cancellationToken: cancellationToken).ConfigureAwait(true);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.InRange(result.Duration.TotalSeconds, 3.8, 4.2);
        Assert.True(await validator.ValidateRequiredStreamsAsync(
            output, requireAudio: true, requireVideo: true,
            cancellationToken).ConfigureAwait(true));
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.True(File.Exists(audio));
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
            cancellationToken: cancellationToken).ConfigureAwait(true);

        Assert.False(result.Succeeded);
        Assert.Equal(invalidAudio, Assert.Single(result.InvalidInputPaths));
        Assert.True(File.Exists(invalidAudio));
        Assert.True(File.Exists(validVideo));
    }

    [Fact]
    [Trait("Category", "FfmpegIntegration")]
    public async Task RequiredStreamValidationDecodesOnlyTheRequestedRealStreams()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var processRunner = new FfmpegProcessRunner();
        if (!await IsToolAvailableAsync(
                processRunner,
                FfmpegExecutableLocator.Ffmpeg,
                cancellationToken).ConfigureAwait(true))
        {
            Assert.Skip("ffmpeg is required for the required-stream integration test.");
        }

        var validator = new FfmpegMediaValidator(processRunner);
        var videoOnly = Path.Combine(_testDirectory, "video-only.mp4");
        await CreateSegmentAsync(
            processRunner,
            videoOnly,
            "green",
            cancellationToken).ConfigureAwait(true);
        Assert.True(await validator.ValidateRequiredStreamsAsync(
            videoOnly,
            requireAudio: false,
            requireVideo: true,
            cancellationToken).ConfigureAwait(true));
        Assert.False(await validator.ValidateRequiredStreamsAsync(
            videoOnly,
            requireAudio: true,
            requireVideo: true,
            cancellationToken).ConfigureAwait(true));

        var audioVideo = Path.Combine(_testDirectory, "audio-video.mp4");
        await CreateAudioVideoAsync(
            processRunner,
            audioVideo,
            cancellationToken).ConfigureAwait(true);
        Assert.True(await validator.ValidateRequiredStreamsAsync(
            audioVideo,
            requireAudio: true,
            requireVideo: true,
            cancellationToken).ConfigureAwait(true));
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

    private static async Task CreateAudioVideoAsync(
        FfmpegProcessRunner processRunner,
        string output,
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
                    "-i", "color=c=blue:s=320x240:r=30:d=1",
                    "-f", "lavfi",
                    "-i", "sine=frequency=1000:duration=1",
                    "-shortest",
                    "-c:v", "libx264",
                    "-pix_fmt", "yuv420p",
                    "-c:a", "aac",
                    output
                ],
                "create-audio-video-fixture"),
            ProcessTimeout,
            cancellationToken).ConfigureAwait(false);

        Assert.True(result.Succeeded, result.StandardError);
    }

    private static async Task CreateAudioOnlyAsync(
        FfmpegProcessRunner processRunner,
        string output,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            new FfmpegCommand(
                FfmpegExecutableLocator.Ffmpeg,
                [
                    "-hide_banner", "-nostdin", "-y",
                    "-f", "lavfi", "-i", "sine=frequency=1000:duration=6",
                    "-c:a", "aac", output
                ],
                "create-independent-audio-fixture"),
            ProcessTimeout,
            cancellationToken).ConfigureAwait(false);

        Assert.True(result.Succeeded, result.StandardError);
    }
}
