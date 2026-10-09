using DownKyi.Core.FFmpeg;

namespace DownKyi.Core.Tests;

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
    public void ConcatWithIndependentAudioMapsOnlyTheSelectedTrack()
    {
        var command = FfmpegCommandFactory.BuildConcat(
            "segments.txt",
            "output.mp4",
            FfmpegConcatStrategy.StreamCopy,
            hardwareEncoder: null,
            embeddedAudioMode: FfmpegEmbeddedAudioMode.Excluded,
            externalAudio: "selected-audio.m4s");

        Assert.Contains("selected-audio.m4s", command.Arguments);
        Assert.Contains("1:a:0", command.Arguments);
        Assert.DoesNotContain("0:a:0", command.Arguments);
        Assert.DoesNotContain("0:a?", command.Arguments);
        Assert.DoesNotContain("-an", command.Arguments);
        Assert.Contains("-shortest", command.Arguments);
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
    public void RequiredStreamDecodeReadsOneNonOptionalUnit()
    {
        var expectations = new[]
        {
            (FfmpegMediaStreamKind.Audio, StreamSpecifier: "0:a:0", FrameLimit: "-frames:a", Operation: "decode-required-audio"),
            (FfmpegMediaStreamKind.Video, StreamSpecifier: "0:v:0", FrameLimit: "-frames:v", Operation: "decode-required-video")
        };

        foreach (var expectation in expectations)
        {
            var command = FfmpegCommandFactory.BuildRequiredStreamDecode(
                "output.mp4",
                expectation.Item1);

            Assert.Equal(expectation.Operation, command.Operation);
            Assert.Contains("-xerror", command.Arguments);
            Assert.Contains(expectation.StreamSpecifier, command.Arguments);
            Assert.DoesNotContain($"{expectation.StreamSpecifier}?", command.Arguments);
            var frameLimitIndex = command.Arguments.ToList().IndexOf(expectation.FrameLimit);
            Assert.True(frameLimitIndex >= 0);
            Assert.Equal("1", command.Arguments[frameLimitIndex + 1]);
            Assert.Contains("pipe:1", command.Arguments);
        }
    }
}
