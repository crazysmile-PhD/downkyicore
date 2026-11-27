using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using DownKyi.Application.Diagnostics;
using DownKyi.Application.Settings;
using Microsoft.Extensions.Logging;

namespace DownKyi.Infrastructure.FFmpeg;

internal sealed record FfmpegHardwareEncoderProfile(
    FfmpegHardwareAcceleration Mode,
    string DisplayName,
    string EncoderName,
    IReadOnlyList<string> OutputArguments);

internal sealed class FfmpegHardwareEncoderDetector
{
    private static readonly TimeSpan DetectionTimeout = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<FfmpegHardwareAcceleration, bool> _usableEncoders = new();
    private readonly IFfmpegProcessRunner _processRunner;
    private readonly ILogger<FfmpegHardwareEncoderDetector> _logger;
    private static readonly IReadOnlyList<string> NvidiaArguments =
        Array.AsReadOnly(new[] { "-c:v", "h264_nvenc", "-preset", "p4", "-cq", "23", "-pix_fmt:v:0", "yuv420p" });
    private static readonly IReadOnlyList<string> IntelQsvArguments =
        Array.AsReadOnly(new[] { "-c:v", "h264_qsv", "-global_quality", "23", "-pix_fmt:v:0", "yuv420p" });
    private static readonly IReadOnlyList<string> AmdAmfArguments =
        Array.AsReadOnly(new[] { "-c:v", "h264_amf", "-quality", "balanced", "-pix_fmt:v:0", "yuv420p" });
    private static readonly IReadOnlyList<string> VaapiArguments = Array.AsReadOnly(new[]
    {
        "-vaapi_device", GetVaapiDevice(),
        "-vf", "format=nv12,hwupload",
        "-c:v", "h264_vaapi",
        "-qp", "23"
    });
    private static readonly IReadOnlyList<string> VideoToolboxArguments =
        Array.AsReadOnly(new[] { "-c:v", "h264_videotoolbox", "-b:v", "6M", "-pix_fmt:v:0", "yuv420p" });

    public FfmpegHardwareEncoderDetector(
        ILogger<FfmpegHardwareEncoderDetector> logger,
        IFfmpegProcessRunner processRunner)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    public async Task<FfmpegHardwareEncoderProfile?> SelectAsync(
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

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_usableEncoders.TryGetValue(candidate.Mode, out var usable))
            {
                var result = await _processRunner.RunAsync(
                    FfmpegCommandFactory.BuildHardwareEncoderProbe(candidate),
                    DetectionTimeout,
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (!result.ProcessStarted || result.TimedOut)
                {
                    _logger.LogInformationMessage(
                        $"FFmpeg hardware probe could not complete: {candidate.DisplayName}; failure={result.ToOperationFailureKind()}");
                    return null;
                }

                usable = result.Succeeded;
                _usableEncoders.TryAdd(candidate.Mode, usable);
                if (!usable)
                {
                    _logger.LogInformationMessage(
                        $"FFmpeg hardware encoder failed its one-frame probe: {candidate.DisplayName}; exit={result.ExitCode}");
                }
            }

            if (usable)
            {
                _logger.LogInformationMessage($"Selected FFmpeg hardware encoder: {candidate.DisplayName}");
                return candidate;
            }
        }

        if (mode != FfmpegHardwareAcceleration.Auto && mode != FfmpegHardwareAcceleration.NotSet)
        {
            _logger.LogInformationMessage($"Requested FFmpeg hardware encoder is unavailable: {mode}");
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

    private static string GetVaapiDevice()
    {
        return "/dev/dri/renderD128";
    }
}
