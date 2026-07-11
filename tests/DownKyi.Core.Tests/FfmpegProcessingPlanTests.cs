using DownKyi.Core.FFMpeg;
using DownKyi.Core.Settings;

namespace DownKyi.Core.Tests;

public sealed class FfmpegProcessingPlanTests
{
    [Fact]
    public void BuildConcatPlanUsesStreamCopyThenHardwareThenCpuFallback()
    {
        var encoder = new FfmpegHardwareEncoderProfile(
            FfmpegHardwareAcceleration.NvidiaNvenc,
            "NVIDIA NVENC",
            "h264_nvenc",
            "-c:v h264_nvenc");

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
