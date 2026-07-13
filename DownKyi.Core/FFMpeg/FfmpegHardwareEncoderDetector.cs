using System.Runtime.InteropServices;
using DownKyi.Core.Logging;
using DownKyi.Core.Settings;

namespace DownKyi.Core.FFMpeg;

internal sealed record FfmpegHardwareEncoderProfile(
    FfmpegHardwareAcceleration Mode,
    string DisplayName,
    string EncoderName,
    IReadOnlyList<string> OutputArguments);

internal static class FfmpegHardwareEncoderDetector
{
    private const string Tag = "FfmpegHardwareEncoderDetector";
    private static readonly char[] EncoderLineSeparators = { '\r', '\n' };
    private static readonly TimeSpan DetectionTimeout = TimeSpan.FromSeconds(5);
    private static readonly Lazy<Task<HashSet<string>>> AvailableEncoders = new(LoadAvailableEncodersAsync);
    private static readonly IReadOnlyList<string> NvidiaArguments =
        Array.AsReadOnly(new[] { "-c:v", "h264_nvenc", "-preset", "p4", "-cq", "23" });
    private static readonly IReadOnlyList<string> IntelQsvArguments =
        Array.AsReadOnly(new[] { "-c:v", "h264_qsv", "-global_quality", "23" });
    private static readonly IReadOnlyList<string> AmdAmfArguments =
        Array.AsReadOnly(new[] { "-c:v", "h264_amf", "-quality", "balanced" });
    private static readonly IReadOnlyList<string> VaapiArguments = Array.AsReadOnly(new[]
    {
        "-vaapi_device", GetVaapiDevice(),
        "-vf", "format=nv12,hwupload",
        "-c:v", "h264_vaapi",
        "-qp", "23"
    });
    private static readonly IReadOnlyList<string> VideoToolboxArguments =
        Array.AsReadOnly(new[] { "-c:v", "h264_videotoolbox", "-b:v", "6M" });

    public static async Task<FfmpegHardwareEncoderProfile?> SelectAsync(
        FfmpegHardwareAcceleration mode,
        CancellationToken cancellationToken = default)
    {
        if (mode == FfmpegHardwareAcceleration.Disabled)
        {
            return null;
        }

        var candidates = mode == FfmpegHardwareAcceleration.Auto
            ? GetAutoCandidates()
            : GetManualCandidates(mode);

        var availableEncoders = await AvailableEncoders.Value
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var candidate in candidates)
        {
            if (availableEncoders.Contains(candidate.EncoderName))
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

    private static FfmpegHardwareEncoderProfile[] GetManualCandidates(FfmpegHardwareAcceleration mode)
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
            NvidiaArguments);
    }

    private static FfmpegHardwareEncoderProfile IntelQsv()
    {
        return new FfmpegHardwareEncoderProfile(
            FfmpegHardwareAcceleration.IntelQsv,
            "Intel QSV",
            "h264_qsv",
            IntelQsvArguments);
    }

    private static FfmpegHardwareEncoderProfile AmdAmf()
    {
        return new FfmpegHardwareEncoderProfile(
            FfmpegHardwareAcceleration.AmdAmf,
            "AMD AMF",
            "h264_amf",
            AmdAmfArguments);
    }

    private static FfmpegHardwareEncoderProfile Vaapi()
    {
        return new FfmpegHardwareEncoderProfile(
            FfmpegHardwareAcceleration.Vaapi,
            "Linux VAAPI",
            "h264_vaapi",
            VaapiArguments);
    }

    private static FfmpegHardwareEncoderProfile VideoToolbox()
    {
        return new FfmpegHardwareEncoderProfile(
            FfmpegHardwareAcceleration.VideoToolbox,
            "macOS VideoToolbox",
            "h264_videotoolbox",
            VideoToolboxArguments);
    }

    private static async Task<HashSet<string>> LoadAvailableEncodersAsync()
    {
        var processRunner = new FfmpegProcessRunner();
        var result = await processRunner.RunAsync(
                new FfmpegCommand(
                    FfmpegExecutableLocator.Ffmpeg,
                    ["-hide_banner", "-encoders"],
                    "detect-hardware-encoders"),
                DetectionTimeout,
                CancellationToken.None)
            .ConfigureAwait(false);
        var output = result.StandardOutput + Environment.NewLine + result.StandardError;
        var encoders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in output.Split(EncoderLineSeparators, StringSplitOptions.RemoveEmptyEntries))
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

    private static string GetVaapiDevice()
    {
        return "/dev/dri/renderD128";
    }
}
