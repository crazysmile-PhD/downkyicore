using System.Diagnostics;
using System.Runtime.InteropServices;
using DownKyi.Core.Logging;
using DownKyi.Core.Settings;

namespace DownKyi.Core.FFMpeg;

internal sealed record FfmpegHardwareEncoderProfile(
    FfmpegHardwareAcceleration Mode,
    string DisplayName,
    string EncoderName,
    string OutputArguments);

internal static class FfmpegHardwareEncoderDetector
{
    private const string Tag = "FfmpegHardwareEncoderDetector";
    private static readonly Lazy<HashSet<string>> AvailableEncoders = new(LoadAvailableEncoders);

    public static FfmpegHardwareEncoderProfile? Select(FfmpegHardwareAcceleration mode)
    {
        if (mode == FfmpegHardwareAcceleration.Disabled)
        {
            return null;
        }

        var candidates = mode == FfmpegHardwareAcceleration.Auto
            ? GetAutoCandidates()
            : GetManualCandidates(mode);

        foreach (var candidate in candidates)
        {
            if (AvailableEncoders.Value.Contains(candidate.EncoderName))
            {
                LogManager.Info(Tag, $"Selected FFmpeg hardware encoder: {candidate.DisplayName}");
                return candidate;
            }
        }

        if (mode != FfmpegHardwareAcceleration.Auto && mode != FfmpegHardwareAcceleration.NotSet)
        {
            LogManager.Info(Tag, $"Requested FFmpeg hardware encoder is unavailable: {mode}");
        }

        return null;
    }

    private static IEnumerable<FfmpegHardwareEncoderProfile> GetAutoCandidates()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            yield return VideoToolbox();
            yield break;
        }

        yield return Nvidia();
        yield return IntelQsv();
        yield return AmdAmf();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && File.Exists(GetVaapiDevice()))
        {
            yield return Vaapi();
        }
    }

    private static IEnumerable<FfmpegHardwareEncoderProfile> GetManualCandidates(FfmpegHardwareAcceleration mode)
    {
        return mode switch
        {
            FfmpegHardwareAcceleration.NvidiaNvenc => new[] { Nvidia() },
            FfmpegHardwareAcceleration.IntelQsv => new[] { IntelQsv() },
            FfmpegHardwareAcceleration.AmdAmf => new[] { AmdAmf() },
            FfmpegHardwareAcceleration.Vaapi => new[] { Vaapi() },
            FfmpegHardwareAcceleration.VideoToolbox => new[] { VideoToolbox() },
            _ => GetAutoCandidates().ToArray()
        };
    }

    private static FfmpegHardwareEncoderProfile Nvidia()
    {
        return new FfmpegHardwareEncoderProfile(
            FfmpegHardwareAcceleration.NvidiaNvenc,
            "NVIDIA NVENC",
            "h264_nvenc",
            "-c:v h264_nvenc -preset p4 -cq 23 -pix_fmt yuv420p");
    }

    private static FfmpegHardwareEncoderProfile IntelQsv()
    {
        return new FfmpegHardwareEncoderProfile(
            FfmpegHardwareAcceleration.IntelQsv,
            "Intel QSV",
            "h264_qsv",
            "-c:v h264_qsv -global_quality 23");
    }

    private static FfmpegHardwareEncoderProfile AmdAmf()
    {
        return new FfmpegHardwareEncoderProfile(
            FfmpegHardwareAcceleration.AmdAmf,
            "AMD AMF",
            "h264_amf",
            "-c:v h264_amf -quality balanced");
    }

    private static FfmpegHardwareEncoderProfile Vaapi()
    {
        return new FfmpegHardwareEncoderProfile(
            FfmpegHardwareAcceleration.Vaapi,
            "Linux VAAPI",
            "h264_vaapi",
            $"-vaapi_device {GetVaapiDevice()} -vf format=nv12,hwupload -c:v h264_vaapi -qp 23");
    }

    private static FfmpegHardwareEncoderProfile VideoToolbox()
    {
        return new FfmpegHardwareEncoderProfile(
            FfmpegHardwareAcceleration.VideoToolbox,
            "macOS VideoToolbox",
            "h264_videotoolbox",
            "-c:v h264_videotoolbox -b:v 6M");
    }

    private static HashSet<string> LoadAvailableEncoders()
    {
        var output = RunFfmpeg("-hide_banner -encoders");
        var encoders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                encoders.Add(parts[1]);
            }
        }

        LogManager.Info(Tag, $"Detected FFmpeg hardware encoders: {string.Join(",", encoders.Where(IsKnownHardwareEncoder))}");
        return encoders;
    }

    private static bool IsKnownHardwareEncoder(string encoder)
    {
        return encoder is "h264_nvenc" or "h264_qsv" or "h264_amf" or "h264_vaapi" or "h264_videotoolbox";
    }

    private static string RunFfmpeg(string arguments)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = GetFfmpegExecutable(),
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(5000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }
            }

            return output + Environment.NewLine + error;
        }
        catch (Exception e)
        {
            LogManager.Error(Tag, e);
            return string.Empty;
        }
    }

    private static string GetFfmpegExecutable()
    {
        var fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffmpeg.exe" : "ffmpeg";
        var bundledPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg", fileName);
        return File.Exists(bundledPath) ? bundledPath : "ffmpeg";
    }

    private static string GetVaapiDevice()
    {
        return "/dev/dri/renderD128";
    }
}
