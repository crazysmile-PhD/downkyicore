using System;
using System.Security.Cryptography;
using System.Text;
using DownKyi.Application.Diagnostics;
using DownKyi.Application.Settings;
using DownKyi.Core.Utils;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

internal sealed class DownloadDiagnosticLogger
{
    public static readonly TimeSpan SpeedLogInterval = TimeSpan.FromSeconds(30);
    private readonly ILogger<DownloadDiagnosticLogger> _logger;

    public DownloadDiagnosticLogger(ILogger<DownloadDiagnosticLogger> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void LogBuiltInTaskStart(
        string source,
        string taskId,
        int urlCount,
        int chunkCount,
        int parallelCount,
        NetworkApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var safeTaskId = ShortId(taskId);
        using var scope = _logger.BeginOperationScope(safeTaskId, safeTaskId);
        _logger.LogInformationMessage(
            $"source={source}; Built-in task started " +
            $"task={safeTaskId}; " +
            $"highSpeed={IsHighSpeedEnabled(settings)}; " +
            $"urlCount={urlCount}; " +
            $"chunkCount={chunkCount}; " +
            $"parallelCount={parallelCount}; " +
            $"globalLimit={settings.MaxOverallDownloadLimit}KB/s; " +
            $"taskLimit={settings.MaxDownloadLimit}KB/s");
    }

    public void LogSpeed(string source, string taskId, long completedLength, long totalLength, long bytesPerSecond)
    {
        if (!_logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        var safeTaskId = ShortId(taskId);
        using var scope = _logger.BeginOperationScope(safeTaskId, safeTaskId);
        _logger.LogInformationMessage(
            $"source={source}; Download speed " +
            $"task={safeTaskId}; " +
            $"speed={Format.FormatSpeedWithBandwidth(bytesPerSecond)}; " +
            $"progress={Format.FormatFileSize(completedLength)}/{Format.FormatFileSize(totalLength)}");
    }

    private static bool IsHighSpeedEnabled(NetworkApplicationSettings settings)
    {
        return settings.HighSpeedDownloadMode == AllowStatus.Yes;
    }

    private static string ShortId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash, 0, 6);
    }
}
