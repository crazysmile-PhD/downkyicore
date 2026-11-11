using DownKyi.Application.Media;
using DownKyi.Infrastructure.FFmpeg;

namespace DownKyi.Infrastructure.Tests;

public sealed class FfmpegCommandFactoryTests
{
    [Fact]
    public void GenericMergePreservesOptionalAudioBehavior()
    {
        var command = FfmpegCommandFactory.BuildMerge(
            audioFile: null,
            videoFile: "segment.flv",
            outputFile: "output.mp4",
            transcodeAudioToMp3: false,
            embeddedAudioMode: FfmpegEmbeddedAudioMode.Optional);

        Assert.Contains("0:a?", command.Arguments);
        Assert.DoesNotContain("0:a:0", command.Arguments);
        Assert.DoesNotContain("-an", command.Arguments);
    }

    [Fact]
    public void MergeVideoOnlyContractDisablesAudioFromDurlInput()
    {
        var command = FfmpegCommandFactory.BuildMerge(
            audioFile: null,
            videoFile: "segment.flv",
            outputFile: "output.mp4",
            transcodeAudioToMp3: false,
            embeddedAudioMode: FfmpegEmbeddedAudioMode.Excluded);

        Assert.Contains("-an", command.Arguments);
        Assert.DoesNotContain("0:a:0", command.Arguments);
        Assert.DoesNotContain("0:a?", command.Arguments);
    }

    [Fact]
    public void MergeAudioVideoContractRequiresAudioFromDurlInput()
    {
        var command = FfmpegCommandFactory.BuildMerge(
            audioFile: null,
            videoFile: "segment.flv",
            outputFile: "output.mp4",
            transcodeAudioToMp3: false,
            embeddedAudioMode: FfmpegEmbeddedAudioMode.Required);

        Assert.Contains("0:a:0", command.Arguments);
        Assert.DoesNotContain("0:a?", command.Arguments);
        Assert.DoesNotContain("-an", command.Arguments);
    }

    [Fact]
    public void ConcatVideoOnlyContractDisablesAudioFromDurlInputs()
    {
        var command = FfmpegCommandFactory.BuildConcat(
            "segments.txt",
            "output.mp4",
            FfmpegConcatStrategy.StreamCopy,
            hardwareEncoder: null,
            embeddedAudioMode: FfmpegEmbeddedAudioMode.Excluded);

        Assert.Contains("-an", command.Arguments);
        Assert.DoesNotContain("0:a:0", command.Arguments);
        Assert.DoesNotContain("0:a?", command.Arguments);
    }

    [Fact]
    public void GenericConcatPreservesOptionalAudioBehavior()
    {
        var command = FfmpegCommandFactory.BuildConcat(
            "segments.txt",
            "output.mp4",
            FfmpegConcatStrategy.StreamCopy,
            hardwareEncoder: null,
            embeddedAudioMode: FfmpegEmbeddedAudioMode.Optional);

        Assert.Contains("0:a?", command.Arguments);
        Assert.DoesNotContain("0:a:0", command.Arguments);
        Assert.DoesNotContain("-an", command.Arguments);
    }

    [Fact]
    public void ConcatTreatsProcessingErrorsAsFatal()
    {
        var command = FfmpegCommandFactory.BuildConcat(
            "segments.txt",
            "output.mp4",
            FfmpegConcatStrategy.CpuEncoder,
            hardwareEncoder: null,
            embeddedAudioMode: FfmpegEmbeddedAudioMode.Optional);

        Assert.Contains("-xerror", command.Arguments);
    }

    [Fact]
    public void MediaMuxCommandsRejectEmptyOutputStreams()
    {
        var merge = FfmpegCommandFactory.BuildMerge(
            audioFile: "audio.m4s",
            videoFile: "video.m4s",
            outputFile: "merged.mp4",
            transcodeAudioToMp3: false,
            embeddedAudioMode: FfmpegEmbeddedAudioMode.Required);
        var concat = FfmpegCommandFactory.BuildConcat(
            "segments.txt",
            "concatenated.mp4",
            FfmpegConcatStrategy.CpuEncoder,
            hardwareEncoder: null,
            embeddedAudioMode: FfmpegEmbeddedAudioMode.Required);

        Assert.Contains("-abort_on", merge.Arguments);
        Assert.Contains("empty_output_stream", merge.Arguments);
        Assert.Contains("-abort_on", concat.Arguments);
        Assert.Contains("empty_output_stream", concat.Arguments);
    }

    [Fact]
    public void ConcatAudioVideoContractRequiresAudioFromDurlInputs()
    {
        var command = FfmpegCommandFactory.BuildConcat(
            "segments.txt",
            "output.mp4",
            FfmpegConcatStrategy.StreamCopy,
            hardwareEncoder: null,
            embeddedAudioMode: FfmpegEmbeddedAudioMode.Required);

        Assert.Contains("0:a:0", command.Arguments);
        Assert.DoesNotContain("0:a?", command.Arguments);
        Assert.DoesNotContain("-an", command.Arguments);
    }

    [Fact]
    public void MergeMapsRequestedCoverAsAttachedPicture()
    {
        var command = FfmpegCommandFactory.BuildMerge(
            audioFile: "audio.m4s",
            videoFile: "video.m4s",
            outputFile: "output.mp4",
            transcodeAudioToMp3: false,
            embeddedAudioMode: FfmpegEmbeddedAudioMode.Required,
            coverFile: "cover.webp");

        AssertInput(command, "cover.webp", expectedInputIndex: 2);
        AssertEmbeddedCover(command, expectedInputIndex: 2);
    }

    [Fact]
    public void ConcatMapsRequestedCoverAsAttachedPicture()
    {
        var command = FfmpegCommandFactory.BuildConcat(
            "segments.txt",
            "output.mp4",
            FfmpegConcatStrategy.CpuEncoder,
            hardwareEncoder: null,
            embeddedAudioMode: FfmpegEmbeddedAudioMode.Required,
            coverFile: "cover.avif");

        AssertInput(command, "cover.avif", expectedInputIndex: 1);
        AssertEmbeddedCover(command, expectedInputIndex: 1);
        AssertOption(command, "-pix_fmt:v:0", "yuv420p");
        Assert.DoesNotContain("-pix_fmt", command.Arguments);
    }

    [Fact]
    public void AudioOnlyMergeDoesNotMapCover()
    {
        var command = FfmpegCommandFactory.BuildMerge(
            audioFile: "audio.m4s",
            videoFile: null,
            outputFile: "output.aac",
            transcodeAudioToMp3: false,
            embeddedAudioMode: FfmpegEmbeddedAudioMode.Excluded,
            coverFile: "cover.jpg");

        Assert.DoesNotContain("cover.jpg", command.Arguments);
        Assert.DoesNotContain("attached_pic", command.Arguments);
    }

    private static void AssertInput(
        FfmpegCommand command,
        string expectedInput,
        int expectedInputIndex)
    {
        var inputs = command.Arguments
            .Select((argument, index) => (argument, index))
            .Where(item => item.argument == "-i")
            .Select(item => command.Arguments[item.index + 1])
            .ToArray();

        Assert.Equal(expectedInput, inputs[expectedInputIndex]);
    }

    private static void AssertEmbeddedCover(FfmpegCommand command, int expectedInputIndex)
    {
        AssertOption(command, "-map", $"{expectedInputIndex}:v:0");
        AssertOption(command, "-c:v:1", "png");
        AssertOption(command, "-frames:v:1", "1");
        AssertOption(command, "-disposition:v:1", "attached_pic");
    }

    private static void AssertOption(FfmpegCommand command, string option, string value)
    {
        Assert.Contains(
            command.Arguments.Select((argument, index) => (argument, index)),
            item => item.argument == option
                    && item.index + 1 < command.Arguments.Count
                    && command.Arguments[item.index + 1] == value);
    }

}
