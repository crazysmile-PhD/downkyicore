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
    public void CoverNormalizationUsesOneDeterministicJpegFrameWithoutExtensionBranching()
    {
        var command = FfmpegCommandFactory.BuildNormalizeCover(
            "cover.unknown",
            "normalized.jpg");

        Assert.Equal("normalize-cover", command.Operation);
        Assert.Contains("-xerror", command.Arguments);
        Assert.Contains("cover.unknown", command.Arguments);
        Assert.Contains("0:v:0", command.Arguments);
        Assert.Contains("-frames:v", command.Arguments);
        Assert.Contains("1", command.Arguments);
        Assert.Contains("mjpeg", command.Arguments);
        Assert.Contains("yuvj420p", command.Arguments);
        Assert.Equal("normalized.jpg", command.Arguments[^1]);
    }

    [Fact]
    public void MediaMuxCommandsEmbedOnlyTheNormalizedJpegCover()
    {
        var merge = FfmpegCommandFactory.BuildMerge(
            audioFile: "audio.m4s",
            videoFile: "video.m4s",
            outputFile: "output.mp4",
            transcodeAudioToMp3: false,
            embeddedAudioMode: FfmpegEmbeddedAudioMode.Required,
            coverFile: "normalized.jpg");
        var concat = FfmpegCommandFactory.BuildConcat(
            "segments.txt",
            "output.mp4",
            FfmpegConcatStrategy.CpuEncoder,
            hardwareEncoder: null,
            embeddedAudioMode: FfmpegEmbeddedAudioMode.Required,
            coverFile: "normalized.jpg");

        Assert.Contains("attached_pic", merge.Arguments);
        Assert.Contains("attached_pic", concat.Arguments);
        Assert.Contains("-c:v:1", merge.Arguments);
        Assert.Contains("-c:v:1", concat.Arguments);
        Assert.Contains("2:v:0", merge.Arguments);
        Assert.Contains("1:v:0", concat.Arguments);
        Assert.Equal(3, merge.Arguments.Count(argument => argument == "-i"));
        Assert.Equal(2, concat.Arguments.Count(argument => argument == "-i"));
        Assert.DoesNotContain("-frames:v", merge.Arguments);
        Assert.DoesNotContain("-frames:v", concat.Arguments);
    }

    [Fact]
    public void LocalMp4MuxCommandsDoNotRelocateMetadataWithFaststart()
    {
        var merge = FfmpegCommandFactory.BuildMerge(
            audioFile: "audio.m4s",
            videoFile: "video.m4s",
            outputFile: "output.mp4",
            transcodeAudioToMp3: false,
            embeddedAudioMode: FfmpegEmbeddedAudioMode.Required,
            coverFile: "normalized.jpg");
        var concat = FfmpegCommandFactory.BuildConcat(
            "segments.txt",
            "output.mp4",
            FfmpegConcatStrategy.StreamCopy,
            hardwareEncoder: null,
            embeddedAudioMode: FfmpegEmbeddedAudioMode.Required,
            coverFile: "normalized.jpg");

        Assert.DoesNotContain("+faststart", merge.Arguments);
        Assert.DoesNotContain("+faststart", concat.Arguments);
    }

}
