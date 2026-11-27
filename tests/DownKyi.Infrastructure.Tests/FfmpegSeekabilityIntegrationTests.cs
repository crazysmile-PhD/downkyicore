using System.Buffers.Binary;
using System.Globalization;
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
    [InlineData("aac", "m4a", "48000", false)]
    [InlineData("flac", "flac", "96000", false)]
    [InlineData("eac3", "mp4", "48000", false)]
    [InlineData("aac", "mp4", "48000", true)]
    [InlineData("flac", "mp4", "96000", true)]
    [InlineData("eac3", "mp4", "48000", true)]
    [Trait("Category", "FfmpegIntegration")]
    public async Task MergePreservesOriginalAudioAndVideoPacketsDespiteLegacyMp3Setting(
        string codec,
        string extension,
        string sampleRate,
        bool includeVideo)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var processRunner = new FfmpegProcessRunner();
        if (!await AreMediaToolsAvailableAsync(processRunner, cancellationToken).ConfigureAwait(true))
        {
            Assert.Skip("ffmpeg and ffprobe are required for the audio quality integration test.");
        }

        var input = Path.Combine(_testDirectory, "audio.m4s");
        var output = Path.Combine(_testDirectory, $"output.{extension}");
        var source = await processRunner.RunAsync(
            new FfmpegCommand(
                FfmpegExecutableLocator.Ffmpeg,
                [
                    "-hide_banner", "-nostdin", "-y",
                    "-f", "lavfi",
                    "-i", $"sine=frequency=1000:sample_rate={sampleRate}:duration=2",
                    "-ac", "2",
                    "-c:a", codec,
                    "-sample_fmt", codec == "flac" ? "s32" : "fltp",
                    "-f", "mp4",
                    input
                ],
                "create-audio-quality-fixture"),
            ProcessTimeout,
            cancellationToken).ConfigureAwait(true);
        Assert.True(source.Succeeded, source.StandardError);
        var settingsPath = Path.Combine(_testDirectory, "audio-settings.json");
        await File.WriteAllTextAsync(
            settingsPath,
            """{"SchemaVersion":1,"Video":{"IsTranscodingAacToMp3":2}}""",
            cancellationToken).ConfigureAwait(true);
        using var settings = new SettingsStore(settingsPath);
        var processor = new FfmpegProcessor(settings, NullLoggerFactory.Instance);
        string? video = null;
        if (includeVideo)
        {
            video = Path.Combine(_testDirectory, "video.mp4");
            await CreateSegmentAsync(processRunner, video, "blue", cancellationToken).ConfigureAwait(true);
        }

        var result = await processor.MergeMediaAsync(
            input,
            video,
            output,
            overwriteDestination: false,
            cancellationToken: cancellationToken).ConfigureAwait(true);

        Assert.True(result.Succeeded, result.FailureReason);
        var probe = await processRunner.RunAsync(
            new FfmpegCommand(
                FfmpegExecutableLocator.Ffprobe,
                [
                    "-v", "error",
                    "-select_streams", "a:0",
                    "-show_entries", "stream=codec_name,sample_rate,channels,bit_rate,bits_per_raw_sample",
                    "-of", "json",
                    output
                ],
                "probe-audio-quality"),
            ProcessTimeout,
            cancellationToken).ConfigureAwait(true);
        Assert.True(probe.Succeeded, probe.StandardError);
        using var document = JsonDocument.Parse(probe.StandardOutput);
        var stream = Assert.Single(document.RootElement.GetProperty("streams").EnumerateArray());
        Assert.Equal(codec, stream.GetProperty("codec_name").GetString());
        Assert.Equal(sampleRate, stream.GetProperty("sample_rate").GetString());
        Assert.Equal(2, stream.GetProperty("channels").GetInt32());
        Assert.Equal(
            await GetPrimaryStreamHashAsync(processRunner, input, cancellationToken).ConfigureAwait(true),
            await GetPrimaryStreamHashAsync(processRunner, output, cancellationToken).ConfigureAwait(true));
        if (video != null)
        {
            Assert.Equal(
                await GetPrimaryStreamHashAsync(processRunner, video, cancellationToken, "0:v:0").ConfigureAwait(true),
                await GetPrimaryStreamHashAsync(processRunner, output, cancellationToken, "0:v:0").ConfigureAwait(true));
        }

        if (codec == "flac")
        {
            Assert.Equal("24", stream.GetProperty("bits_per_raw_sample").GetString());
        }

        if (extension == "m4a")
        {
            var seek = await processRunner.RunAsync(
                new FfmpegCommand(
                    FfmpegExecutableLocator.Ffmpeg,
                    ["-v", "error", "-ss", "1", "-i", output, "-map", "0:a:0", "-t", "0.1", "-f", "framemd5", "-"],
                    "seek-original-aac"),
                ProcessTimeout,
                cancellationToken).ConfigureAwait(true);
            Assert.True(seek.Succeeded, seek.StandardError);
            Assert.Contains("0,", seek.StandardOutput, StringComparison.Ordinal);
        }
    }

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
    public async Task CorruptCoverIsClassifiedFromFfmpegDecodeFailure()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var processRunner = new FfmpegProcessRunner();
        if (!await IsToolAvailableAsync(
                processRunner,
                FfmpegExecutableLocator.Ffmpeg,
                cancellationToken).ConfigureAwait(true))
        {
            Assert.Skip("ffmpeg is required for the cover classification integration test.");
        }

        var audio = Path.Combine(_testDirectory, "classification-audio.m4s");
        var video = Path.Combine(_testDirectory, "classification-video.m4s");
        var cover = Path.Combine(_testDirectory, "classification-cover.download");
        await File.WriteAllBytesAsync(audio, [1, 2, 3], cancellationToken).ConfigureAwait(true);
        await File.WriteAllBytesAsync(video, [1, 2, 3], cancellationToken).ConfigureAwait(true);
        await File.WriteAllBytesAsync(cover, [1, 2, 3], cancellationToken).ConfigureAwait(true);
        using var settings = new SettingsStore(
            Path.Combine(_testDirectory, "classification-settings.json"));
        var processor = new FfmpegProcessor(settings, NullLoggerFactory.Instance);

        var result = await processor.MergeMediaAsync(
            audio,
            video,
            Path.Combine(_testDirectory, "classification-output.mp4"),
            overwriteDestination: false,
            cover: cover,
            cancellationToken: cancellationToken).ConfigureAwait(true);

        Assert.False(result.Succeeded);
        Assert.Equal(FfmpegOperationFailureKind.InputInvalid, result.FailureKind);
        Assert.Equal(FfmpegOperationFailureStage.Cover, result.FailureStage);
        Assert.DoesNotContain(
            Directory.EnumerateFiles(_testDirectory),
            file => Path.GetFileName(file).Contains("downkyi-cover", StringComparison.Ordinal));
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
            resolveHardwareEncoder: null,
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "FfmpegIntegration")]
    public async Task KnownDurlTimelineCopiesOriginalStreamsAndSeeksIntoTheLastSegment(bool wrongContainerDuration)
    {
        var token = TestContext.Current.CancellationToken;
        var realRunner = new FfmpegProcessRunner();
        if (!await AreMediaToolsAvailableAsync(realRunner, token).ConfigureAwait(true))
        {
            Assert.Skip("ffmpeg and ffprobe are required for the DURL remux regression.");
        }

        var first = Path.Combine(_testDirectory, "first.flv");
        var second = Path.Combine(_testDirectory, "second.flv");
        foreach (var (path, color) in new[] { (first, "red"), (second, "blue") })
        {
            var source = await realRunner.RunAsync(new FfmpegCommand(FfmpegExecutableLocator.Ffmpeg,
                ["-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i", $"color=c={color}:s=320x240:r=30:d=2",
                    "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=2",
                    "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", path], "create-durl-segment"),
                ProcessTimeout, token).ConfigureAwait(true);
            Assert.True(source.Succeeded, source.StandardError);
        }

        var originalFrames = (await GetFrameHashesAsync(realRunner, first, token).ConfigureAwait(true))
            .Concat(await GetFrameHashesAsync(realRunner, second, token).ConfigureAwait(true)).ToArray();
        var originalAudio = (await GetAudioPacketHashesAsync(realRunner, first, token).ConfigureAwait(true))
            .Concat(await GetAudioPacketHashesAsync(realRunner, second, token).ConfigureAwait(true)).ToArray();
        // Preserve independent source timing before damaging the header. The
        // lavfi content duration is not the duration of the muxed media segment.
        var firstDuration = await GetDurationSecondsAsync(realRunner, first, token).ConfigureAwait(true);
        var secondDuration = await GetDurationSecondsAsync(realRunner, second, token).ConfigureAwait(true);
        if (wrongContainerDuration)
        {
            var bytes = await File.ReadAllBytesAsync(first, token).ConfigureAwait(true);
            var durationOffset = bytes.AsSpan().IndexOf("\0\bduration\0"u8);
            Assert.True(durationOffset >= 0);
            BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(durationOffset + 11, 8),
                BitConverter.DoubleToInt64Bits(12));
            await File.WriteAllBytesAsync(first, bytes, token).ConfigureAwait(true);

            // The old list omits duration directives: FFmpeg succeeds but leaves
            // a ten-second gap. Exit code and a frame hash alone miss this bug.
            var oldList = Path.Combine(_testDirectory, "old-list.txt");
            await File.WriteAllLinesAsync(oldList, ["file 'first.flv'", "file 'second.flv'"], token)
                .ConfigureAwait(true);
            var oldOutput = Path.Combine(_testDirectory, "old-timeline.mp4");
            var oldResult = await realRunner.RunAsync(FfmpegCommandFactory.BuildConcat(
                oldList, oldOutput, FfmpegConcatStrategy.StreamCopy, null, FfmpegEmbeddedAudioMode.Required),
                ProcessTimeout, token).ConfigureAwait(true);
            Assert.True(oldResult.Succeeded, oldResult.StandardError);
            Assert.True(await GetDurationSecondsAsync(realRunner, oldOutput, token).ConfigureAwait(true) > 10);
            Assert.True(await GetSeekFrameOffsetAsync(realRunner, oldOutput, 3.5, token).ConfigureAwait(true) > 1);
        }

        var runner = new RecordingMediaRunner(realRunner);
        using var settings = new SettingsStore(Path.Combine(_testDirectory, "durl-remux-settings.json"));
        var processor = new FfmpegProcessor(settings, NullLoggerFactory.Instance, runner);
        var output = Path.Combine(_testDirectory, "lossless-durl.mp4");
        var result = await processor.ConcatDurlVideosAsync(settings.Current.Video,
            [new(2, second, TimeSpan.FromSeconds(secondDuration)), new(1, first, TimeSpan.FromSeconds(firstDuration))],
            output, overwriteDestination: false, embeddedAudioMode: FfmpegEmbeddedAudioMode.Required,
            cancellationToken: token).ConfigureAwait(true);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(["probe-concat-input", "probe-concat-input", "concat-StreamCopy"], runner.Operations);
        Assert.InRange(await GetDurationSecondsAsync(realRunner, output, token).ConfigureAwait(true),
            firstDuration + secondDuration - 0.15, firstDuration + secondDuration + 0.15);
        Assert.Equal(originalFrames, await GetFrameHashesAsync(realRunner, output, token).ConfigureAwait(true));
        Assert.Equal(originalAudio, await GetAudioPacketHashesAsync(realRunner, output, token).ConfigureAwait(true));
        await AssertCanSeekAsync(realRunner, output, token).ConfigureAwait(true);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
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
        var primaryAudioHash = await GetPrimaryStreamHashAsync(
            processRunner,
            audio,
            cancellationToken).ConfigureAwait(true);
        var result = await processor.MergeMediaAsync(
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
            await GetPrimaryStreamHashAsync(
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
                new FfmpegConcatSegment(2, second, TimeSpan.FromSeconds(2)),
                new FfmpegConcatSegment(1, first, TimeSpan.FromSeconds(2))
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "FfmpegIntegration")]
    public async Task CorruptSegmentDoesNotCommitConcatOutput(bool copy)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var processRunner = new FfmpegProcessRunner();
        if (!await AreMediaToolsAvailableAsync(processRunner, cancellationToken).ConfigureAwait(true))
        {
            Assert.Skip("ffmpeg and ffprobe are required for the concat integration test.");
        }

        var clean = Path.Combine(_testDirectory, "clean.ts");
        var corrupt = Path.Combine(_testDirectory, "corrupt.ts");
        await CreateTransportStreamSegmentAsync(processRunner, clean, cancellationToken).ConfigureAwait(true);
        await CreateCorruptSegmentAsync(processRunner, clean, corrupt, cancellationToken).ConfigureAwait(true);

        var recordingRunner = new RecordingMediaRunner(processRunner);
        var runtime = new FfmpegConcatRuntime(
            recordingRunner,
            new AsyncConcurrencyGate(() => 1),
            NullLogger<FfmpegConcatRuntime>.Instance);
        var output = Path.Combine(_testDirectory, "must-not-exist.mp4");

        var result = await runtime.ConcatAsync(
            [
                new FfmpegConcatSegment(1, clean, TimeSpan.FromSeconds(2)),
                new FfmpegConcatSegment(2, corrupt, TimeSpan.FromSeconds(2))
            ],
            output,
            resolveHardwareEncoder: null,
            allowStreamCopy: copy,
            overwriteDestination: true,
            embeddedAudioMode: FfmpegEmbeddedAudioMode.Excluded,
            cancellationToken: cancellationToken).ConfigureAwait(true);

        Assert.False(result.Succeeded);
        Assert.Equal(FfmpegOperationFailureKind.InputInvalid, result.FailureKind);
        Assert.False(File.Exists(output));
        Assert.True(File.Exists(clean));
        Assert.True(File.Exists(corrupt));
        Assert.Equal(copy ? "concat-StreamCopy" : "concat-CpuEncoder", recordingRunner.Operations[^1]);
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
            resolveHardwareEncoder: null,
            allowStreamCopy: false,
            overwriteDestination: true,
            embeddedAudioMode: FfmpegEmbeddedAudioMode.Required,
            cancellationToken: cancellationToken).ConfigureAwait(true);

        Assert.False(mergeResult.Succeeded);
        Assert.Equal(FfmpegOperationFailureKind.ProcessFailure, mergeResult.FailureKind);
        Assert.False(File.Exists(mergeOutput));
        Assert.False(concatResult.Succeeded);
        Assert.Equal(FfmpegOperationFailureKind.InputInvalid, concatResult.FailureKind);
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

    private static async Task<string> GetPrimaryStreamHashAsync(
        FfmpegProcessRunner processRunner,
        string mediaFile,
        CancellationToken cancellationToken,
        string stream = "0:a:0")
    {
        var result = await processRunner.RunAsync(
            new FfmpegCommand(
                FfmpegExecutableLocator.Ffmpeg,
                [
                    "-v", "error",
                    "-i", mediaFile,
                    "-map", stream,
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
        var duration = await GetDurationSecondsAsync(processRunner, mediaFile, cancellationToken).ConfigureAwait(false);
        foreach (var timestamp in new[] { duration / 2, duration * 0.9 })
        {
            var offset = await GetSeekFrameOffsetAsync(processRunner, mediaFile, timestamp, cancellationToken)
                .ConfigureAwait(false);
            Assert.InRange(offset, 0, 0.1);
        }
    }

    private static async Task<double> GetDurationSecondsAsync(
        FfmpegProcessRunner runner, string path, CancellationToken token)
    {
        var result = await runner.RunAsync(new FfmpegCommand(FfmpegExecutableLocator.Ffprobe,
            ["-v", "error", "-show_entries", "format=duration", "-of", "json", path], "probe-duration"),
            ProcessTimeout, token).ConfigureAwait(false);
        Assert.True(result.Succeeded, result.StandardError);
        using var document = JsonDocument.Parse(result.StandardOutput);
        return double.Parse(document.RootElement.GetProperty("format").GetProperty("duration").GetString()!,
            CultureInfo.InvariantCulture);
    }

    private static async Task<double> GetSeekFrameOffsetAsync(
        FfmpegProcessRunner runner, string path, double timestamp, CancellationToken token)
    {
        var result = await runner.RunAsync(new FfmpegCommand(FfmpegExecutableLocator.Ffmpeg,
            ["-v", "error", "-ss", timestamp.ToString("0.#######", CultureInfo.InvariantCulture), "-i", path,
                "-map", "0:v:0", "-frames:v", "1", "-f", "framehash", "-hash", "sha256", "-"], "seek-primary-video"),
            ProcessTimeout, token).ConfigureAwait(false);
        Assert.True(result.Succeeded, result.StandardError);
        var lines = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var row = Assert.Single(lines, line => !line.StartsWith('#', StringComparison.Ordinal));
        var timeBase = Assert.Single(lines, line => line.StartsWith("#tb 0:", StringComparison.Ordinal))
            ["#tb 0:".Length..].Trim().Split('/');
        var pts = long.Parse(row.Split(',')[2].Trim(), CultureInfo.InvariantCulture);
        return pts * double.Parse(timeBase[0], CultureInfo.InvariantCulture) /
            double.Parse(timeBase[1], CultureInfo.InvariantCulture);
    }

    private static async Task<string[]> GetFrameHashesAsync(
        FfmpegProcessRunner runner, string path, CancellationToken token)
    {
        var result = await runner.RunAsync(new FfmpegCommand(FfmpegExecutableLocator.Ffmpeg,
            ["-v", "error", "-i", path, "-map", "0:v:0", "-fps_mode", "passthrough",
                "-f", "framehash", "-hash", "sha256", "-"], "hash-decoded-video-frames"),
            ProcessTimeout, token).ConfigureAwait(false);
        Assert.True(result.Succeeded, result.StandardError);
        var hashes = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.StartsWith('#', StringComparison.Ordinal)).Select(line => line.Split(',')[^1].Trim()).ToArray();
        Assert.NotEmpty(hashes);
        return hashes;
    }

    private static async Task<string[]> GetAudioPacketHashesAsync(
        FfmpegProcessRunner runner, string path, CancellationToken token)
    {
        var result = await runner.RunAsync(new FfmpegCommand(FfmpegExecutableLocator.Ffprobe,
            ["-v", "error", "-select_streams", "a:0", "-show_packets", "-show_data_hash", "sha256",
                "-show_entries", "packet=data_hash", "-of", "json", path], "hash-original-audio-packets"),
            ProcessTimeout, token).ConfigureAwait(false);
        Assert.True(result.Succeeded, result.StandardError);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var hashes = document.RootElement.GetProperty("packets").EnumerateArray()
            .Select(packet => packet.GetProperty("data_hash").GetString()!).ToArray();
        Assert.NotEmpty(hashes);
        return hashes;
    }

    private sealed class RecordingMediaRunner(FfmpegProcessRunner inner) : IFfmpegProcessRunner
    {
        public List<string> Operations { get; } = [];

        public Task<FfmpegProcessResult> RunAsync(
            FfmpegCommand command, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Operations.Add(command.Operation);
            return inner.RunAsync(command, timeout, cancellationToken);
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
