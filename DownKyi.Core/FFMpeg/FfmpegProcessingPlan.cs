namespace DownKyi.Core.FFMpeg;

internal enum FfmpegConcatStrategy
{
    StreamCopy,
    HardwareEncoder,
    CpuEncoder
}

internal static class FfmpegProcessingPlan
{
    private static readonly IReadOnlyList<FfmpegConcatStrategy> DurlWithoutHardware = new[]
    {
        FfmpegConcatStrategy.CpuEncoder
    };

    private static readonly IReadOnlyList<FfmpegConcatStrategy> DurlWithHardware = new[]
    {
        FfmpegConcatStrategy.HardwareEncoder,
        FfmpegConcatStrategy.CpuEncoder
    };

    private static readonly IReadOnlyList<FfmpegConcatStrategy> WithoutHardware = new[]
    {
        FfmpegConcatStrategy.StreamCopy,
        FfmpegConcatStrategy.CpuEncoder
    };

    private static readonly IReadOnlyList<FfmpegConcatStrategy> WithHardware = new[]
    {
        FfmpegConcatStrategy.StreamCopy,
        FfmpegConcatStrategy.HardwareEncoder,
        FfmpegConcatStrategy.CpuEncoder
    };

    public static IReadOnlyList<FfmpegConcatStrategy> BuildConcatPlan(
        FfmpegHardwareEncoderProfile? hardwareEncoder,
        bool allowStreamCopy = true)
    {
        if (!allowStreamCopy)
        {
            return hardwareEncoder == null
                ? DurlWithoutHardware
                : DurlWithHardware;
        }

        return hardwareEncoder == null
            ? WithoutHardware
            : WithHardware;
    }
}
