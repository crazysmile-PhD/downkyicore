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

    public static TheoryData<string, string> CoverFixtures => new()
    {
        {
            "jpeg",
            "/9j/4AAQSkZJRgABAgAAAQABAAD//gAQTGF2YzYzLjE1LjEwMAD/2wBDAAgEBAQEBAUFBQUFBQYGBgYGBgYGBgYGBgYHBwcICAgHBwcGBgcHCAgICAkJCQgICAgJCQoKCgwMCwsODg4RERT/xABMAAEBAAAAAAAAAAAAAAAAAAAABgEBAQAAAAAAAAAAAAAAAAAABgcQAQAAAAAAAAAAAAAAAAAAAAARAQAAAAAAAAAAAAAAAAAAAAD/wAARCAAQABADASIAAhEAAxEA/9oADAMBAAIRAxEAPwCLAE1/f//Z"
        },
        {
            "png",
            "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAIAAACQkWg2AAAACXBIWXMAAAABAAAAAQBPJcTWAAAAF0lEQVR4nGP8y0AaYCFR/aiGUQ1DSAMAaAABO/WgKQIAAAAASUVORK5CYII="
        },
        {
            "bmp",
            "Qk02AwAAAAAAADYAAAAoAAAAEAAAABAAAAABABgAAAAAAAADAAAAAAAAAAAAAAAAAAAAAAAAAAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9AAD9"
        },
        {
            "gif",
            "R0lGODlhEAAQAPcfMQAAACQAAEgAAGwAAJAAALQAANgAAPwAAAAkACQkAEgkAGwkAJAkALQkANgkAPwkAABIACRIAEhIAGxIAJBIALRIANhIAPxIAABsACRsAEhsAGxsAJBsALRsANhsAPxsAACQACSQAEiQAGyQAJCQALSQANiQAPyQAAC0ACS0AEi0AGy0AJC0ALS0ANi0APy0AADYACTYAEjYAGzYAJDYALTYANjYAPzYAAD8ACT8AEj8AGz8AJD8ALT8ANj8APz8AAAAVSQAVUgAVWwAVZAAVbQAVdgAVfwAVQAkVSQkVUgkVWwkVZAkVbQkVdgkVfwkVQBIVSRIVUhIVWxIVZBIVbRIVdhIVfxIVQBsVSRsVUhsVWxsVZBsVbRsVdhsVfxsVQCQVSSQVUiQVWyQVZCQVbSQVdiQVfyQVQC0VSS0VUi0VWy0VZC0VbS0Vdi0Vfy0VQDYVSTYVUjYVWzYVZDYVbTYVdjYVfzYVQD8VST8VUj8VWz8VZD8VbT8Vdj8Vfz8VQAAqiQAqkgAqmwAqpAAqrQAqtgAqvwAqgAkqiQkqkgkqmwkqpAkqrQkqtgkqvwkqgBIqiRIqkhIqmxIqpBIqrRIqthIqvxIqgBsqiRsqkhsqmxsqpBsqrRsqthsqvxsqgCQqiSQqkiQqmyQqpCQqrSQqtiQqvyQqgC0qiS0qki0qmy0qpC0qrS0qti0qvy0qgDYqiTYqkjYqmzYqpDYqrTYqtjYqvzYqgD8qiT8qkj8qmz8qpD8qrT8qtj8qvz8qgAA/yQA/0gA/2wA/5AA/7QA/9gA//wA/wAk/yQk/0gk/2wk/5Ak/7Qk/9gk//wk/wBI/yRI/0hI/2xI/5BI/7RI/9hI//xI/wBs/yRs/0hs/2xs/5Bs/7Rs/9hs//xs/wCQ/ySQ/0iQ/2yQ/5CQ/7SQ/9iQ//yQ/wC0/yS0/0i0/2y0/5C0/7S0/9i0//y0/wDY/yTY/0jY/2zY/5DY/7TY/9jY//zY/wD8/yT8/0j8/2z8/5D8/7T8/9j8//z8/yH/C05FVFNDQVBFMi4wAwEAAAAh+QQEBAAfACwAAAAAEAAQAAAIJwAPCBxIsKDBgwgTDjRwkKHChxAjQnRYkKLEixgXNszIsWPFjQIDAgA7"
        },
        {
            "webp",
            "UklGRjAAAABXRUJQVlA4ICQAAABwAQCdASoQABAAD8D+JaACdAFAAAD+y7XJ+sg/FDzW9gyoAAA="
        },
        {
            "avif",
            "AAAAIGZ0eXBhdmlmAAAAAGF2aWZtaWYxbWlhZk1BMUIAAAD5bWV0YQAAAAAAAAAvaGRscgAAAAAAAAAAcGljdAAAAAAAAAAAAAAAAFBpY3R1cmVIYW5kbGVyAAAAAA5waXRtAAAAAAABAAAAHmlsb2MAAAAARAAAAQABAAAAAQAAASEAAAAdAAAAKGlpbmYAAAAAAAEAAAAaaW5mZQIAAAAAAQAAYXYwMUNvbG9yAAAAAGppcHJwAAAAS2lwY28AAAAUaXNwZQAAAAAAAAAQAAAAEAAAABBwaXhpAAAAAAMICAgAAAAMYXYxQ4EADAAAAAATY29scm5jbHgAAgACAAIAAAAAF2lwbWEAAAAAAAAAAQABBAECgwQAAAAlbWRhdAoGGAz/2wCAMhMYAAAAUAAAAACpjmy2qrHGtoVA"
        }
    };

    [Theory]
    [MemberData(nameof(CoverFixtures))]
    [Trait("Category", "FfmpegIntegration")]
    public async Task CoverNormalizationUsesActualContentForEverySupportedInput(
        string format,
        string base64)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var processRunner = new FfmpegProcessRunner();
        if (!await AreMediaToolsAvailableAsync(processRunner, cancellationToken).ConfigureAwait(true))
        {
            Assert.Skip("ffmpeg and ffprobe are required for the cover integration test.");
        }

        var input = Path.Combine(_testDirectory, $"cover-{format}.download");
        var output = Path.Combine(_testDirectory, $"cover-{format}.jpg");
        await File.WriteAllBytesAsync(
            input,
            Convert.FromBase64String(base64),
            cancellationToken).ConfigureAwait(true);

        var result = await processRunner.RunAsync(
            FfmpegCommandFactory.BuildNormalizeCover(input, output),
            ProcessTimeout,
            cancellationToken).ConfigureAwait(true);

        Assert.True(result.Succeeded, result.StandardError);
        Assert.True(File.Exists(output));
        Assert.NotEqual(0, new FileInfo(output).Length);
        await AssertOnlyJpegVideoAsync(
            processRunner,
            output,
            cancellationToken).ConfigureAwait(true);
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
        await AssertCanSeekAsync(
            processRunner,
            output,
            cancellationToken).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Category", "FfmpegIntegration")]
    public async Task DashMergeNormalizesMislabeledPngAndEmbedsJpegInTheOnlyMediaMux()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var processRunner = new FfmpegProcessRunner();
        if (!await AreMediaToolsAvailableAsync(processRunner, cancellationToken).ConfigureAwait(true))
        {
            Assert.Skip("ffmpeg and ffprobe are required for the cover integration test.");
        }

        var video = Path.Combine(_testDirectory, "cover-merge-video.mp4");
        var audio = Path.Combine(_testDirectory, "cover-merge-audio.m4a");
        var cover = Path.Combine(_testDirectory, "cover-merge.webp");
        var output = Path.Combine(_testDirectory, "cover-merge-output.mp4");
        await CreateSegmentAsync(processRunner, video, "red", cancellationToken).ConfigureAwait(true);
        await CreateAudioAsync(processRunner, audio, cancellationToken).ConfigureAwait(true);
        await File.WriteAllBytesAsync(
            cover,
            Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="),
            cancellationToken).ConfigureAwait(true);
        using var settings = new SettingsStore(Path.Combine(_testDirectory, "cover-merge-settings.json"));
        var processor = new FfmpegProcessor(
            settings,
            NullLoggerFactory.Instance,
            processRunner);

        var primaryVideoHash = await GetPrimaryVideoHashAsync(
            processRunner,
            video,
            cancellationToken).ConfigureAwait(true);
        var primaryAudioHash = await GetPrimaryAudioHashAsync(
            processRunner,
            audio,
            cancellationToken).ConfigureAwait(true);
        var result = await processor.MergeMediaAsync(
            settings.Current.Video,
            audio,
            video,
            destination: output,
            overwriteDestination: true,
            embeddedAudioMode: FfmpegEmbeddedAudioMode.Excluded,
            cover: cover,
            cancellationToken).ConfigureAwait(true);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(
            primaryVideoHash,
            await GetPrimaryVideoHashAsync(
                processRunner,
                output,
                cancellationToken).ConfigureAwait(true));
        Assert.Equal(
            primaryAudioHash,
            await GetPrimaryAudioHashAsync(
                processRunner,
                output,
                cancellationToken).ConfigureAwait(true));
        await AssertAttachedCoverAsync(
            processRunner,
            output,
            expectedCodec: "mjpeg",
            cancellationToken: cancellationToken).ConfigureAwait(true);
        await AssertCanSeekAsync(
            processRunner,
            output,
            cancellationToken).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Category", "FfmpegIntegration")]
    public async Task MultiSegmentDurlNormalizesCoverBeforeTheOnlyMediaMux()
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
        await File.WriteAllBytesAsync(
            cover,
            Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="),
            cancellationToken).ConfigureAwait(true);
        using var settings = new SettingsStore(Path.Combine(_testDirectory, "cover-concat-settings.json"));
        var processor = new FfmpegProcessor(
            settings,
            NullLoggerFactory.Instance,
            processRunner);

        var result = await processor.ConcatDurlVideosAsync(
            settings.Current.Video,
            [
                new FfmpegConcatSegment(2, second),
                new FfmpegConcatSegment(1, first)
            ],
            outputVideo: output,
            overwriteDestination: true,
            embeddedAudioMode: FfmpegEmbeddedAudioMode.Excluded,
            cover: cover,
            cancellationToken: cancellationToken).ConfigureAwait(true);

        Assert.True(result.Succeeded, result.FailureReason);
        await AssertAttachedCoverAsync(
            processRunner,
            output,
            expectedCodec: "mjpeg",
            cancellationToken: cancellationToken).ConfigureAwait(true);
        await AssertCanSeekAsync(
            processRunner,
            output,
            cancellationToken).ConfigureAwait(true);
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

    private static async Task AssertOnlyJpegVideoAsync(
        FfmpegProcessRunner processRunner,
        string imageFile,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            new FfmpegCommand(
                FfmpegExecutableLocator.Ffprobe,
                [
                    "-v", "error",
                    "-show_entries", "stream=codec_name,codec_type",
                    "-of", "json",
                    imageFile
                ],
                "probe-normalized-cover"),
            ProcessTimeout,
            cancellationToken).ConfigureAwait(false);
        Assert.True(result.Succeeded, result.StandardError);

        using var document = JsonDocument.Parse(result.StandardOutput);
        var stream = Assert.Single(
            document.RootElement.GetProperty("streams").EnumerateArray());
        Assert.Equal("mjpeg", stream.GetProperty("codec_name").GetString());
        Assert.Equal("video", stream.GetProperty("codec_type").GetString());
    }

    private static async Task AssertAttachedCoverAsync(
        FfmpegProcessRunner processRunner,
        string mediaFile,
        string expectedCodec,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            new FfmpegCommand(
                FfmpegExecutableLocator.Ffprobe,
                [
                    "-v", "error",
                    "-count_packets",
                    "-show_entries", "stream=codec_name,codec_type,nb_read_packets:stream_disposition=attached_pic",
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
            stream.GetProperty("codec_name").GetString() == expectedCodec
            && stream.GetProperty("disposition").GetProperty("attached_pic").GetInt32() == 1);
        var primaryVideo = Assert.Single(streams, stream =>
            stream.GetProperty("codec_type").GetString() == "video"
            && stream.GetProperty("disposition").GetProperty("attached_pic").GetInt32() == 0);
        Assert.True(
            int.TryParse(primaryVideo.GetProperty("nb_read_packets").GetString(), out var packetCount),
            "ffprobe did not report the primary video packet count.");
        Assert.True(
            packetCount > 1,
            $"Expected the primary video to contain more than one packet, but found {packetCount}.");
    }

    private static async Task<string> GetPrimaryVideoHashAsync(
        FfmpegProcessRunner processRunner,
        string mediaFile,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            new FfmpegCommand(
                FfmpegExecutableLocator.Ffmpeg,
                [
                    "-v", "error",
                    "-i", mediaFile,
                    "-map", "0:v:0",
                    "-c", "copy",
                    "-f", "hash",
                    "-hash", "sha256",
                    "-"
                ],
                "hash-primary-video"),
            ProcessTimeout,
            cancellationToken).ConfigureAwait(false);
        Assert.True(result.Succeeded, result.StandardError);
        return result.StandardOutput.Trim();
    }

    private static async Task<string> GetPrimaryAudioHashAsync(
        FfmpegProcessRunner processRunner,
        string mediaFile,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            new FfmpegCommand(
                FfmpegExecutableLocator.Ffmpeg,
                [
                    "-v", "error",
                    "-i", mediaFile,
                    "-map", "0:a:0",
                    "-c", "copy",
                    "-f", "hash",
                    "-hash", "sha256",
                    "-"
                ],
                "hash-primary-audio"),
            ProcessTimeout,
            cancellationToken).ConfigureAwait(false);
        Assert.True(result.Succeeded, result.StandardError);
        return result.StandardOutput.Trim();
    }

    private static async Task AssertCanSeekAsync(
        FfmpegProcessRunner processRunner,
        string mediaFile,
        CancellationToken cancellationToken)
    {
        foreach (var timestamp in new[] { "1", "1.9" })
        {
            var result = await processRunner.RunAsync(
                new FfmpegCommand(
                    FfmpegExecutableLocator.Ffmpeg,
                    [
                        "-v", "error",
                        "-ss", timestamp,
                        "-i", mediaFile,
                        "-map", "0:v:0",
                        "-frames:v", "1",
                        "-f", "null",
                        "-"
                    ],
                    "seek-primary-video"),
                ProcessTimeout,
                cancellationToken).ConfigureAwait(false);
            Assert.True(result.Succeeded, result.StandardError);
        }
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

    private static async Task CreateAudioAsync(
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
                    "-i", "sine=frequency=1000:duration=2",
                    "-c:a", "aac",
                    output
                ],
                "create-test-audio"),
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
