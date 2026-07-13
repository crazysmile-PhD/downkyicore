using System.Globalization;

namespace DownKyi.Core.FFMpeg;

internal static class FfmpegCommandFactory
{
    public static FfmpegCommand BuildConcat(
        string listFile,
        string outputFile,
        FfmpegConcatStrategy strategy,
        FfmpegHardwareEncoderProfile? hardwareEncoder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFile);

        var arguments = new List<string>
        {
            "-hide_banner",
            "-nostdin",
            "-y",
            "-fflags",
            "+genpts",
            "-f",
            "concat",
            "-safe",
            "0",
            "-i",
            listFile,
            "-map",
            "0:v:0",
            "-map",
            "0:a?"
        };

        switch (strategy)
        {
            case FfmpegConcatStrategy.StreamCopy:
                arguments.AddRange(["-c:v", "copy", "-c:a", "copy"]);
                break;
            case FfmpegConcatStrategy.HardwareEncoder:
                ArgumentNullException.ThrowIfNull(hardwareEncoder);

                arguments.AddRange(hardwareEncoder.OutputArguments);
                arguments.AddRange(["-c:a", "aac"]);
                break;
            case FfmpegConcatStrategy.CpuEncoder:
                arguments.AddRange([
                    "-c:v", "libx264",
                    "-preset", "veryfast",
                    "-crf", "23",
                    "-threads", "2",
                    "-c:a", "aac"
                ]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(strategy), strategy, "Unsupported concat strategy.");
        }

        arguments.AddRange([
            "-pix_fmt", "yuv420p",
            "-avoid_negative_ts", "make_zero",
            "-movflags", "+faststart",
            outputFile
        ]);

        return new FfmpegCommand(FfmpegExecutableLocator.Ffmpeg, arguments, $"concat-{strategy}");
    }

    public static FfmpegCommand BuildProbe(string mediaFile)
    {
        return new FfmpegCommand(
            FfmpegExecutableLocator.Ffprobe,
            [
                "-v", "error",
                "-show_entries", "stream=codec_type:format=duration",
                "-of", "json",
                mediaFile
            ],
            "probe-media");
    }

    public static FfmpegCommand BuildSeekDecode(string mediaFile, TimeSpan position)
    {
        return new FfmpegCommand(
            FfmpegExecutableLocator.Ffmpeg,
            [
                "-hide_banner",
                "-nostdin",
                "-v", "error",
                "-xerror",
                "-ss", position.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                "-i", mediaFile,
                "-map", "0:v:0",
                "-frames:v", "1",
                "-progress", "pipe:1",
                "-nostats",
                "-f", "null",
                "-"
            ],
            "seek-decode");
    }
}
