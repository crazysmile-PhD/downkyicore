namespace DownKyi.Core.FFMpeg;

internal enum FfmpegConcatStrategy
{
    StreamCopy,
    HardwareEncoder,
    CpuEncoder
}

internal static class FfmpegProcessingPlan
{
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
        FfmpegHardwareEncoderProfile? hardwareEncoder)
    {
        return hardwareEncoder == null
            ? WithoutHardware
            : WithHardware;
    }
}
