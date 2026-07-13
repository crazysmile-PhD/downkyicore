using System.Globalization;

namespace DownKyi.Core.FFMpeg;

internal static class FfmpegCommandFactory
{
    public static FfmpegCommand BuildMerge(
        string? audioFile,
        string? videoFile,
        string outputFile,
        bool transcodeAudioToMp3)
    {
        var arguments = CreateBaseArguments();
        if (audioFile != null)
        {
            arguments.AddRange(["-i", audioFile]);
        }

        if (videoFile != null)
        {
            arguments.AddRange(["-i", videoFile]);
        }

        if (audioFile != null && videoFile != null)
        {
            arguments.AddRange(["-map", "1:v:0", "-map", "0:a:0", "-c:v", "copy", "-c:a", "copy"]);
        }
        else if (videoFile != null)
        {
            arguments.AddRange(["-map", "0:v:0", "-map", "0:a?", "-c", "copy"]);
        }
        else if (transcodeAudioToMp3)
        {
            arguments.AddRange(["-vn", "-c:a", "libmp3lame"]);
        }
        else
        {
            arguments.AddRange(["-vn", "-c:a", "copy"]);
        }

        if (videoFile != null && string.Equals(Path.GetExtension(outputFile), ".mp4", StringComparison.OrdinalIgnoreCase))
        {
            arguments.AddRange(["-movflags", "+faststart"]);
        }

        arguments.Add(outputFile);
        return new FfmpegCommand(FfmpegExecutableLocator.Ffmpeg, arguments, "merge-media");
    }

    public static FfmpegCommand BuildExtractAudio(string inputFile, string outputFile)
    {
        var arguments = CreateBaseArguments();
        arguments.AddRange(["-i", inputFile, "-vn", "-c:a", "copy", outputFile]);
        return new FfmpegCommand(FfmpegExecutableLocator.Ffmpeg, arguments, "extract-audio");
    }

    public static FfmpegCommand BuildExtractVideo(string inputFile, string outputFile)
    {
        var arguments = CreateBaseArguments();
        arguments.AddRange(["-i", inputFile, "-an", "-c:v", "copy", outputFile]);
        return new FfmpegCommand(FfmpegExecutableLocator.Ffmpeg, arguments, "extract-video");
    }

    public static FfmpegCommand BuildDelogo(
        string inputFile,
        string outputFile,
        int x,
        int y,
        int width,
        int height)
    {
        var arguments = CreateBaseArguments();
        arguments.AddRange([
            "-i", inputFile,
            "-vf", $"delogo=x={x}:y={y}:w={width}:h={height}:show=0",
            outputFile
        ]);
        return new FfmpegCommand(FfmpegExecutableLocator.Ffmpeg, arguments, "delogo");
    }

    public static FfmpegCommand BuildExtractFrame(
        string inputFile,
        string outputFile,
        TimeSpan timestamp)
    {
        var arguments = CreateBaseArguments();
        arguments.AddRange([
            "-ss", timestamp.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", inputFile,
            "-frames:v", "1",
            "-c:v", "mjpeg",
            outputFile
        ]);
        return new FfmpegCommand(FfmpegExecutableLocator.Ffmpeg, arguments, "extract-frame");
    }

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

    private static List<string> CreateBaseArguments()
    {
        return ["-hide_banner", "-nostdin", "-y"];
    }
}
