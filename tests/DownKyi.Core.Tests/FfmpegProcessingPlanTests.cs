using DownKyi.Core.FFMpeg;
using DownKyi.Core.Settings;

namespace DownKyi.Core.Tests;

public sealed class FfmpegProcessingPlanTests
{
    private static readonly IReadOnlyList<string> NvidiaArguments =
        Array.AsReadOnly(new[] { "-c:v", "h264_nvenc" });

    [Fact]
    public void BuildConcatPlanUsesStreamCopyThenHardwareThenCpuFallback()
    {
        var encoder = new FfmpegHardwareEncoderProfile(
            FfmpegHardwareAcceleration.NvidiaNvenc,
            "NVIDIA NVENC",
            "h264_nvenc",
            NvidiaArguments);

        var plan = FfmpegProcessingPlan.BuildConcatPlan(encoder);

        Assert.Equal(
            new[]
            {
                FfmpegConcatStrategy.StreamCopy,
                FfmpegConcatStrategy.HardwareEncoder,
                FfmpegConcatStrategy.CpuEncoder
            },
            plan);
    }

    [Fact]
    public void BuildConcatPlanSkipsStreamCopyForMultiSegmentDurl()
    {
        var encoder = new FfmpegHardwareEncoderProfile(
            FfmpegHardwareAcceleration.NvidiaNvenc,
            "NVIDIA NVENC",
            "h264_nvenc",
            NvidiaArguments);

        var plan = FfmpegProcessingPlan.BuildConcatPlan(encoder, allowStreamCopy: false);

        Assert.Equal(
            new[]
            {
                FfmpegConcatStrategy.HardwareEncoder,
                FfmpegConcatStrategy.CpuEncoder
            },
            plan);
    }

    [Fact]
    public void BuildConcatPlanSkipsHardwareButKeepsCpuFallbackWhenUnavailable()
    {
        var plan = FfmpegProcessingPlan.BuildConcatPlan(hardwareEncoder: null);

        Assert.Equal(
            new[]
            {
                FfmpegConcatStrategy.StreamCopy,
                FfmpegConcatStrategy.CpuEncoder
            },
            plan);
    }
}
