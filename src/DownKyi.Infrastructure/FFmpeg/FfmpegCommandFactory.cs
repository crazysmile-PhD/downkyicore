using System.Globalization;
using DownKyi.Application.Media;

namespace DownKyi.Infrastructure.FFmpeg;

internal enum FfmpegConcatStrategy
{
    StreamCopy,
    HardwareEncoder,
    CpuEncoder
}

internal static class FfmpegCommandFactory
{
    // A 128x128 black PNG uses the bundled decoder without an input device or temporary file.
    private const string HardwareProbeFrame = "data:image/png;base64," +
        "iVBORw0KGgoAAAANSUhEUgAAAIAAAACACAIAAABMXPacAAAARklEQVR4nO3BAQEAAACCIP+vbkhAAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABA9wbAgAABWam+hQAAAABJRU5ErkJggg==";

    public static FfmpegCommand BuildMerge(
        string? audioFile,
        string? videoFile,
        string outputFile,
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
        else
        {
            arguments.AddRange(["-vn", "-c:a", "copy"]);
        }

        if (coverInputIndex >= 0)
        {
            AddEmbeddedJpegCover(arguments, coverInputIndex);
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

    public static FfmpegCommand BuildNormalizeCover(string inputFile, string outputFile)
    {
        var arguments = CreateBaseArguments();
        arguments.Add("-xerror");
        AddRequiredOutputContract(arguments);
        arguments.AddRange([
            "-i", inputFile,
            "-map", "0:v:0",
            "-frames:v", "1",
            "-c:v", "mjpeg",
            "-q:v", "2",
            "-pix_fmt", "yuvj420p",
            outputFile
        ]);
        return new FfmpegCommand(FfmpegExecutableLocator.Ffmpeg, arguments, "normalize-cover");
    }

    public static FfmpegCommand BuildConcatInputProbe(string inputFile)
    {
        return new FfmpegCommand(FfmpegExecutableLocator.Ffprobe,
        [
            "-v", "error", "-show_streams", "-show_data_hash", "sha256",
            "-of", "json", inputFile
        ], "probe-concat-input");
    }

    public static FfmpegCommand BuildHardwareEncoderProbe(FfmpegHardwareEncoderProfile encoder)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        var arguments = CreateBaseArguments();
        AddRequiredOutputContract(arguments);
        arguments.AddRange([
            "-v", "error", "-xerror", "-i", HardwareProbeFrame,
            "-frames:v", "1", "-an"
        ]);
        arguments.AddRange(encoder.OutputArguments);
        arguments.AddRange(["-f", "null", "-"]);
        return new FfmpegCommand(FfmpegExecutableLocator.Ffmpeg, arguments, "probe-hardware-encoder");
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

        var arguments = CreateBaseArguments();
        arguments.Add("-xerror");
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

        AddConcatStreamMapping(arguments, embeddedAudioMode);
        if (strategy == FfmpegConcatStrategy.StreamCopy)
        {
            // Stream copy alone never decodes damaged packets. Validate the same
            // selected streams in this process while preserving their packets in
            // the MP4 output; a decode error still aborts the operation.
            arguments.AddRange(["-f", "null", "-"]);
            AddConcatStreamMapping(arguments, embeddedAudioMode);
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
                    "-pix_fmt:v:0", "yuv420p",
                    "-c:a", "aac"
                ]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(strategy), strategy, "Unsupported concat strategy.");
        }

        if (coverFile != null)
        {
            AddEmbeddedJpegCover(arguments, inputIndex: 1);
        }

        arguments.AddRange([
            "-avoid_negative_ts", "make_zero",
            outputFile
        ]);

        return new FfmpegCommand(FfmpegExecutableLocator.Ffmpeg, arguments, $"concat-{strategy}");
    }

    private static void AddConcatStreamMapping(List<string> arguments, FfmpegEmbeddedAudioMode audioMode)
    {
        arguments.AddRange(["-map", "0:v:0"]);
        if (audioMode == FfmpegEmbeddedAudioMode.Required)
        {
            arguments.AddRange(["-map", "0:a:0"]);
        }
        else if (audioMode == FfmpegEmbeddedAudioMode.Excluded)
        {
            arguments.Add("-an");
        }
        else
        {
            arguments.AddRange(["-map", "0:a?"]);
        }
    }

    private static List<string> CreateBaseArguments()
    {
        return ["-hide_banner", "-nostdin", "-nostats", "-y"];
    }

    private static void AddRequiredOutputContract(List<string> arguments)
    {
        arguments.AddRange(["-abort_on", "empty_output_stream"]);
    }

    private static void AddEmbeddedJpegCover(List<string> arguments, int inputIndex)
    {
        arguments.AddRange([
            "-map", $"{inputIndex}:v:0",
            "-c:v:1", "copy",
            "-disposition:v:1", "attached_pic"
        ]);
    }

}
