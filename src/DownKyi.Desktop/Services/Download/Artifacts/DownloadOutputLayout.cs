using System;
using System.Collections.Generic;
using System.IO;
using DownKyi.Application.Downloads;
using DownKyi.Application.Settings;
using DownKyi.Domain.Downloads;

namespace DownKyi.Services.Download;

internal static class DownloadOutputLayout
{
    private const int FlacAudioCodecId = 30251;

    public static string GetDashMediaPath(
        string outputBasePath,
        bool hasVideo,
        int audioCodecId,
        AllowStatus transcodeAacToMp3)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputBasePath);
        if (hasVideo)
        {
            return outputBasePath + ".mp4";
        }

        if (transcodeAacToMp3 == AllowStatus.Yes)
        {
            return outputBasePath + ".mp3";
        }

        return audioCodecId == FlacAudioCodecId
            ? outputBasePath + ".flac"
            : outputBasePath + ".aac";
    }

    public static string GetDurlMediaPath(string outputBasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputBasePath);
        return outputBasePath + ".mp4";
    }

    public static IReadOnlyList<string> GetReusableStagedMediaPaths(
        string outputBasePath,
        bool needsVideo,
        bool needsAudio)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputBasePath);
        return needsVideo
            ? [outputBasePath + ".mp4"]
            : needsAudio
                ? [
                    outputBasePath + ".mp3",
                    outputBasePath + ".aac",
                    outputBasePath + ".flac"
                ]
                : [];
    }

    public static IReadOnlyList<DownloadDanmakuOutputPath> GetDanmakuOutputPaths(
        string outputBasePath,
        DanmakuOutputFormat outputFormat)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputBasePath);
        var outputs = new List<DownloadDanmakuOutputPath>(2);
        if (outputFormat.IncludesAss())
        {
            outputs.Add(new DownloadDanmakuOutputPath(
                DownloadDanmakuOutputKind.Ass,
                outputBasePath + ".ass"));
        }

        if (outputFormat.IncludesXml())
        {
            outputs.Add(new DownloadDanmakuOutputPath(
                DownloadDanmakuOutputKind.Xml,
                outputBasePath + ".xml"));
        }

        return outputs;
    }

    public static string GetSubtitleTrackPath(string outputBasePath, string languageDescription)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputBasePath);
        ArgumentNullException.ThrowIfNull(languageDescription);
        return $"{outputBasePath}_{languageDescription}.srt";
    }

    public static string GetDefaultSubtitlePath(string outputBasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputBasePath);
        return outputBasePath + ".srt";
    }

    public static string GetNfoPath(string outputBasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputBasePath);
        return outputBasePath + ".nfo";
    }

    public static string GetPageCoverPath(string outputBasePath, string? sourceAddress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputBasePath);
        return $"{outputBasePath}.{GetSourceExtension(sourceAddress)}";
    }

    public static string GetMainCoverPath(string outputBasePath, string? sourceAddress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputBasePath);
        return $"{outputBasePath}.Cover.{GetSourceExtension(sourceAddress)}";
    }

    public static string GetSourceExtension(string? sourceAddress)
    {
        if (string.IsNullOrWhiteSpace(sourceAddress))
        {
            return string.Empty;
        }

        var candidate = sourceAddress.StartsWith("//", StringComparison.Ordinal)
            ? $"{Uri.UriSchemeHttps}:{sourceAddress}"
            : sourceAddress;
        var path = Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            ? uri.AbsolutePath
            : sourceAddress.Split('?', '#')[0];
        return Path.GetExtension(path).TrimStart('.');
    }

    public static bool IsMediaExtension(string extension) =>
        extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
        || IsAudioExtension(extension);

    public static bool IsAudioExtension(string extension) =>
        extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".aac", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".flac", StringComparison.OrdinalIgnoreCase);

    public static bool IsImageExtension(string extension) =>
        extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".gif", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".avif", StringComparison.OrdinalIgnoreCase);

    public static bool HasOutputClaimConflict(
        string outputBasePath,
        string existingFile,
        DownloadActionClaims requestedClaims)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputBasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(existingFile);

        var directory = Path.GetDirectoryName(outputBasePath);
        if (directory == null)
        {
            return false;
        }

        var baseKey = CreateComparisonKey(outputBasePath);
        var fileKey = CreateComparisonKey(existingFile);
        var stemKey = CreateComparisonKey(
            Path.Combine(directory, Path.GetFileNameWithoutExtension(existingFile)));
        var extension = Path.GetExtension(existingFile);
        if (requestedClaims.Contains(DownloadActionClaim.Media)
            && PlatformComparer.Equals(stemKey, baseKey)
            && IsMediaExtension(extension))
        {
            return true;
        }

        if (requestedClaims.Contains(DownloadActionClaim.Subtitle)
            && extension.Equals(".srt", StringComparison.OrdinalIgnoreCase)
            && (PlatformComparer.Equals(stemKey, baseKey)
                || stemKey.StartsWith(baseKey + "_", PlatformComparison)))
        {
            return true;
        }

        if ((requestedClaims.Contains(DownloadActionClaim.DanmakuAss)
             && PlatformComparer.Equals(fileKey, baseKey + ".ass"))
            || (requestedClaims.Contains(DownloadActionClaim.DanmakuXml)
                && PlatformComparer.Equals(fileKey, baseKey + ".xml")))
        {
            return true;
        }

        if (requestedClaims.Contains(DownloadActionClaim.Nfo)
            && PlatformComparer.Equals(fileKey, baseKey + ".nfo"))
        {
            return true;
        }

        return requestedClaims.Contains(DownloadActionClaim.Cover)
               && IsImageExtension(extension)
               && (PlatformComparer.Equals(stemKey, baseKey)
                   || stemKey.StartsWith(baseKey + ".Cover", PlatformComparison));
    }

    private static string CreateComparisonKey(string path) =>
        DownloadOutputPathKey.Create(path, ignoreCase: false);

    private static StringComparer PlatformComparer =>
        DownloadOutputPathKey.UsesCaseInsensitiveComparison
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static StringComparison PlatformComparison =>
        DownloadOutputPathKey.UsesCaseInsensitiveComparison
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

}

internal enum DownloadDanmakuOutputKind
{
    Ass,
    Xml
}

internal readonly record struct DownloadDanmakuOutputPath(
    DownloadDanmakuOutputKind Kind,
    string File);
