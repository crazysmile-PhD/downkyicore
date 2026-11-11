using System.Text.Json;
using DownKyi.Application.Media;
using DownKyi.Infrastructure.FFmpeg;
using DownKyi.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Infrastructure.Tests;

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
    public async Task MultiSegmentDurlOutputIsFinalizedAfterFfmpegSuccess()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var processRunner = new FfmpegProcessRunner();
        if (!await IsToolAvailableAsync(
                processRunner,
                FfmpegExecutableLocator.Ffmpeg,
                cancellationToken).ConfigureAwait(true))
        {
            Assert.Skip("ffmpeg is required for the concat integration test.");
        }

        var first = Path.Combine(_testDirectory, "segment-1.mp4");
        var second = Path.Combine(_testDirectory, "segment-2.mp4");
        await CreateSegmentAsync(processRunner, first, "red", cancellationToken).ConfigureAwait(true);
        await CreateSegmentAsync(processRunner, second, "blue", cancellationToken).ConfigureAwait(true);

        var runtime = new FfmpegConcatRuntime(
            processRunner,
            new AsyncConcurrencyGate(() => 1),
            NullLogger<FfmpegConcatRuntime>.Instance);
        var output = Path.Combine(_testDirectory, "seekable.mp4");

        var result = await runtime.ConcatAsync(
            [
                new FfmpegConcatSegment(2, second),
                new FfmpegConcatSegment(1, first)
            ],
            output,
            hardwareEncoder: null,
            allowStreamCopy: false,
            overwriteDestination: true,
            cancellationToken: cancellationToken).ConfigureAwait(true);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.True(File.Exists(output));
        Assert.NotEqual(0, new FileInfo(output).Length);
    }

    [Fact]
    [Trait("Category", "FfmpegIntegration")]
    public async Task DashMergeEmbedsRequestedCoverAsAttachedPicture()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var processRunner = new FfmpegProcessRunner();
        if (!await AreMediaToolsAvailableAsync(processRunner, cancellationToken).ConfigureAwait(true))
        {
            Assert.Skip("ffmpeg and ffprobe are required for the cover integration test.");
        }

        var video = Path.Combine(_testDirectory, "cover-merge-video.mp4");
        var cover = Path.Combine(_testDirectory, "cover-merge.png");
        var output = Path.Combine(_testDirectory, "cover-merge-output.mp4");
        await CreateSegmentAsync(processRunner, video, "red", cancellationToken).ConfigureAwait(true);
        await CreateCoverAsync(processRunner, cover, cancellationToken).ConfigureAwait(true);
        using var settings = new SettingsStore(Path.Combine(_testDirectory, "cover-merge-settings.json"));
        var processor = new FfmpegProcessor(
            settings,
            NullLoggerFactory.Instance,
            processRunner);

        var result = await processor.MergeMediaAsync(
            settings.Current.Video,
            audio: null,
            video,
            destination: output,
            overwriteDestination: true,
            embeddedAudioMode: FfmpegEmbeddedAudioMode.Excluded,
            cover,
            cancellationToken).ConfigureAwait(true);

        Assert.True(result.Succeeded, result.FailureReason);
        await AssertAttachedCoverAsync(processRunner, output, cancellationToken).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Category", "FfmpegIntegration")]
    public async Task MultiSegmentDurlEmbedsRequestedCoverAsAttachedPicture()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var processRunner = new FfmpegProcessRunner();
        if (!await AreMediaToolsAvailableAsync(processRunner, cancellationToken).ConfigureAwait(true))
        {
            Assert.Skip("ffmpeg and ffprobe are required for the cover integration test.");
        }

        var first = Path.Combine(_testDirectory, "cover-segment-1.mp4");
        var second = Path.Combine(_testDirectory, "cover-segment-2.mp4");
        var cover = Path.Combine(_testDirectory, "cover-concat.png");
        var output = Path.Combine(_testDirectory, "cover-concat-output.mp4");
        await CreateSegmentAsync(processRunner, first, "red", cancellationToken).ConfigureAwait(true);
        await CreateSegmentAsync(processRunner, second, "blue", cancellationToken).ConfigureAwait(true);
        await CreateCoverAsync(processRunner, cover, cancellationToken).ConfigureAwait(true);
        var runtime = new FfmpegConcatRuntime(
            processRunner,
            new AsyncConcurrencyGate(() => 1),
            NullLogger<FfmpegConcatRuntime>.Instance);

        var result = await runtime.ConcatAsync(
            [
                new FfmpegConcatSegment(2, second),
                new FfmpegConcatSegment(1, first)
            ],
            output,
            hardwareEncoder: null,
            allowStreamCopy: false,
            overwriteDestination: true,
            embeddedAudioMode: FfmpegEmbeddedAudioMode.Excluded,
            coverFile: cover,
            cancellationToken: cancellationToken).ConfigureAwait(true);

        Assert.True(result.Succeeded, result.FailureReason);
        await AssertAttachedCoverAsync(processRunner, output, cancellationToken).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Category", "FfmpegIntegration")]
    public async Task CorruptSegmentDoesNotCommitConcatOutput()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var processRunner = new FfmpegProcessRunner();
        if (!await IsToolAvailableAsync(
                processRunner,
                FfmpegExecutableLocator.Ffmpeg,
                cancellationToken).ConfigureAwait(true))
        {
            Assert.Skip("ffmpeg is required for the concat integration test.");
        }

        var clean = Path.Combine(_testDirectory, "clean.ts");
        var corrupt = Path.Combine(_testDirectory, "corrupt.ts");
        await CreateTransportStreamSegmentAsync(processRunner, clean, cancellationToken).ConfigureAwait(true);
        await CreateCorruptSegmentAsync(processRunner, clean, corrupt, cancellationToken).ConfigureAwait(true);

        var runtime = new FfmpegConcatRuntime(
            processRunner,
            new AsyncConcurrencyGate(() => 1),
            NullLogger<FfmpegConcatRuntime>.Instance);
        var output = Path.Combine(_testDirectory, "must-not-exist.mp4");

        var result = await runtime.ConcatAsync(
            [
                new FfmpegConcatSegment(1, clean),
                new FfmpegConcatSegment(2, corrupt)
            ],
            output,
            hardwareEncoder: null,
            allowStreamCopy: false,
            overwriteDestination: true,
            embeddedAudioMode: FfmpegEmbeddedAudioMode.Excluded,
            cancellationToken: cancellationToken).ConfigureAwait(true);

        Assert.False(result.Succeeded);
        Assert.Equal(FfmpegOperationFailureKind.ProcessFailure, result.FailureKind);
        Assert.False(File.Exists(output));
    }

    [Fact]
    [Trait("Category", "FfmpegIntegration")]
    public async Task EmptyRequiredStreamDoesNotCommitMediaOutput()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var processRunner = new FfmpegProcessRunner();
        if (!await IsToolAvailableAsync(
                processRunner,
                FfmpegExecutableLocator.Ffmpeg,
                cancellationToken).ConfigureAwait(true))
        {
            Assert.Skip("ffmpeg is required for the media output integration test.");
        }

        var emptyVideo = Path.Combine(_testDirectory, "empty-video.mkv");
        await CreateEmptyVideoStreamSegmentAsync(
                processRunner,
                emptyVideo,
                cancellationToken)
            .ConfigureAwait(true);

        using var settings = new SettingsStore(Path.Combine(_testDirectory, "settings.json"));
        var processor = new FfmpegProcessor(
            settings,
            NullLoggerFactory.Instance,
            processRunner);
        var mergeOutput = Path.Combine(_testDirectory, "merge-must-not-exist.mp4");
        var mergeResult = await processor.MergeMediaAsync(
            settings.Current.Video,
            audio: null,
            video: emptyVideo,
            destination: mergeOutput,
            overwriteDestination: true,
            embeddedAudioMode: FfmpegEmbeddedAudioMode.Required,
            cancellationToken: cancellationToken).ConfigureAwait(true);

        var concatRuntime = new FfmpegConcatRuntime(
            processRunner,
            new AsyncConcurrencyGate(() => 1),
            NullLogger<FfmpegConcatRuntime>.Instance);
        var concatOutput = Path.Combine(_testDirectory, "concat-must-not-exist.mp4");
        var concatResult = await concatRuntime.ConcatAsync(
            [new FfmpegConcatSegment(1, emptyVideo)],
            concatOutput,
            hardwareEncoder: null,
            allowStreamCopy: false,
            overwriteDestination: true,
            embeddedAudioMode: FfmpegEmbeddedAudioMode.Required,
            cancellationToken: cancellationToken).ConfigureAwait(true);

        Assert.False(mergeResult.Succeeded);
        Assert.Equal(FfmpegOperationFailureKind.ProcessFailure, mergeResult.FailureKind);
        Assert.False(File.Exists(mergeOutput));
        Assert.False(concatResult.Succeeded);
        Assert.Equal(FfmpegOperationFailureKind.ProcessFailure, concatResult.FailureKind);
        Assert.False(File.Exists(concatOutput));
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

    private static async Task<bool> AreMediaToolsAvailableAsync(
        FfmpegProcessRunner processRunner,
        CancellationToken cancellationToken) =>
        await IsToolAvailableAsync(
            processRunner,
            FfmpegExecutableLocator.Ffmpeg,
            cancellationToken).ConfigureAwait(false)
        && await IsToolAvailableAsync(
            processRunner,
            FfmpegExecutableLocator.Ffprobe,
            cancellationToken).ConfigureAwait(false);

    private static async Task AssertAttachedCoverAsync(
        FfmpegProcessRunner processRunner,
        string mediaFile,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            new FfmpegCommand(
                FfmpegExecutableLocator.Ffprobe,
                [
                    "-v", "error",
                    "-show_entries", "stream=codec_name:stream_disposition=attached_pic",
                    "-of", "json",
                    mediaFile
                ],
                "probe-attached-cover"),
            ProcessTimeout,
            cancellationToken).ConfigureAwait(false);
        Assert.True(result.Succeeded, result.StandardError);

        using var document = JsonDocument.Parse(result.StandardOutput);
        var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        Assert.Contains(streams, stream =>
            stream.GetProperty("codec_name").GetString() == "png"
            && stream.GetProperty("disposition").GetProperty("attached_pic").GetInt32() == 1);
        Assert.Contains(streams, stream =>
            stream.GetProperty("disposition").GetProperty("attached_pic").GetInt32() == 0);
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

    private static async Task CreateCoverAsync(
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
                    "-i", "color=c=orange:s=640x360:d=0.04",
                    "-frames:v", "1",
                    "-c:v", "png",
                    output
                ],
                "create-test-cover"),
            ProcessTimeout,
            cancellationToken).ConfigureAwait(false);

        Assert.True(result.Succeeded, result.StandardError);
    }

    private static async Task CreateTransportStreamSegmentAsync(
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
                    "-i", "testsrc2=s=320x240:r=30:d=2",
                    "-c:v", "libx264",
                    "-g", "60",
                    "-pix_fmt", "yuv420p",
                    "-f", "mpegts",
                    output
                ],
                "create-transport-stream-segment"),
            ProcessTimeout,
            cancellationToken).ConfigureAwait(false);

        Assert.True(result.Succeeded, result.StandardError);
    }

    private static async Task CreateCorruptSegmentAsync(
        FfmpegProcessRunner processRunner,
        string input,
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
                    "-i", input,
                    "-map", "0:v:0",
                    "-c:v", "copy",
                    "-bsf:v", "noise=amount='if(eq(n,30),10,0)'",
                    "-f", "mpegts",
                    output
                ],
                "create-corrupt-test-segment"),
            ProcessTimeout,
            cancellationToken).ConfigureAwait(false);

        Assert.True(result.Succeeded, result.StandardError);
    }

    private static async Task CreateEmptyVideoStreamSegmentAsync(
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
                    "-i", "anullsrc=r=48000:cl=stereo:d=1",
                    "-f", "lavfi",
                    "-i", "color=c=black:s=320x240:r=30:d=1",
                    "-map", "0:a:0",
                    "-map", "1:v:0",
                    "-vf", "select=0",
                    "-c:a", "aac",
                    "-c:v", "libx264",
                    output
                ],
                "create-empty-video-stream-segment"),
            ProcessTimeout,
            cancellationToken).ConfigureAwait(false);

        Assert.True(result.Succeeded, result.StandardError);
    }

}
