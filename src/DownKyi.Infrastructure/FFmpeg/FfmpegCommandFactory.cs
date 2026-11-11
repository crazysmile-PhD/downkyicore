using System.Globalization;
using DownKyi.Application.Media;

namespace DownKyi.Infrastructure.FFmpeg;

internal static class FfmpegCommandFactory
{
    public static FfmpegCommand BuildMerge(
        string? audioFile,
        string? videoFile,
        string outputFile,
        bool transcodeAudioToMp3,
        FfmpegEmbeddedAudioMode embeddedAudioMode,
        string? coverFile = null)
    {
        var arguments = CreateBaseArguments();
        AddRequiredOutputContract(arguments);
        if (audioFile != null)
        {
            arguments.AddRange(["-i", audioFile]);
        }

        if (videoFile != null)
        {
            arguments.AddRange(["-i", videoFile]);
        }

        var coverInputIndex = -1;
        if (videoFile != null && coverFile != null)
        {
            coverInputIndex = audioFile != null ? 2 : 1;
            arguments.AddRange(["-i", coverFile]);
        }

        if (audioFile != null && videoFile != null)
        {
            arguments.AddRange(["-map", "1:v:0", "-map", "0:a:0", "-c:v", "copy", "-c:a", "copy"]);
        }
        else if (videoFile != null)
        {
            arguments.AddRange(["-map", "0:v:0"]);
            if (embeddedAudioMode == FfmpegEmbeddedAudioMode.Required)
            {
                arguments.AddRange(["-map", "0:a:0", "-c:v", "copy", "-c:a", "copy"]);
            }
            else if (embeddedAudioMode == FfmpegEmbeddedAudioMode.Excluded)
            {
                arguments.AddRange(["-an", "-c:v", "copy"]);
            }
            else
            {
                arguments.AddRange(["-map", "0:a?", "-c", "copy"]);
            }
        }
        else if (transcodeAudioToMp3)
        {
            arguments.AddRange(["-vn", "-c:a", "libmp3lame"]);
        }
        else
        {
            arguments.AddRange(["-vn", "-c:a", "copy"]);
        }

        if (coverInputIndex >= 0)
        {
            AddEmbeddedCover(arguments, coverInputIndex);
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
        FfmpegHardwareEncoderProfile? hardwareEncoder,
        FfmpegEmbeddedAudioMode embeddedAudioMode,
        string? coverFile = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFile);

        var arguments = new List<string>
        {
            "-hide_banner",
            "-nostdin",
            "-y",
            "-xerror",
        };
        AddRequiredOutputContract(arguments);
        arguments.AddRange([
            "-fflags", "+genpts",
            "-f",
            "concat",
            "-safe",
            "0",
            "-i",
            listFile
        ]);
        if (coverFile != null)
        {
            arguments.AddRange(["-i", coverFile]);
        }

        arguments.AddRange(["-map", "0:v:0"]);
        if (embeddedAudioMode == FfmpegEmbeddedAudioMode.Required)
        {
            arguments.AddRange(["-map", "0:a:0"]);
        }
        else if (embeddedAudioMode == FfmpegEmbeddedAudioMode.Excluded)
        {
            arguments.Add("-an");
        }
        else
        {
            arguments.AddRange(["-map", "0:a?"]);
        }

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

        if (coverFile != null)
        {
            AddEmbeddedCover(arguments, inputIndex: 1);
        }

        arguments.AddRange([
            "-pix_fmt:v:0", "yuv420p",
            "-avoid_negative_ts", "make_zero",
            "-movflags", "+faststart",
            outputFile
        ]);

        return new FfmpegCommand(FfmpegExecutableLocator.Ffmpeg, arguments, $"concat-{strategy}");
    }

    private static List<string> CreateBaseArguments()
    {
        return ["-hide_banner", "-nostdin", "-y"];
    }

    private static void AddRequiredOutputContract(List<string> arguments)
    {
        arguments.AddRange(["-abort_on", "empty_output_stream"]);
    }

    private static void AddEmbeddedCover(List<string> arguments, int inputIndex)
    {
        arguments.AddRange([
            "-map", $"{inputIndex}:v:0",
            "-c:v:1", "png",
            "-frames:v:1", "1",
            "-disposition:v:1", "attached_pic"
        ]);
    }
}
