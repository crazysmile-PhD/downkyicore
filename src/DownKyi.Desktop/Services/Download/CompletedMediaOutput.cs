using System;
using System.Collections.Generic;
using System.IO;
using DownKyi.Models;

namespace DownKyi.Services.Download;

internal static class CompletedMediaOutput
{
    private static readonly string[] VideoExtensions = [".mp4", ".flv"];
    private static readonly string[] AudioExtensions = [".aac", ".mp3", ".flac"];
    private static readonly string[] CoverExtensions = [".jpg", ".jpeg", ".png", ".webp", ".avif", ".gif"];

    public static bool Exists(DownloadBase downloadBase)
    {
        ArgumentNullException.ThrowIfNull(downloadBase);
        if (string.IsNullOrWhiteSpace(downloadBase.FilePath))
        {
            return false;
        }

        if (IsRequested(downloadBase, "downloadVideo"))
        {
            return HasNonEmptyFile(downloadBase.FilePath, VideoExtensions);
        }

        if (IsRequested(downloadBase, "downloadAudio"))
        {
            return HasNonEmptyFile(downloadBase.FilePath, AudioExtensions);
        }

        return (IsRequested(downloadBase, "downloadDanmaku")
                && HasNonEmptyFile(downloadBase.FilePath, [".ass"]))
            || (IsRequested(downloadBase, "downloadSubtitle")
                && HasSubtitle(downloadBase.FilePath))
            || (IsRequested(downloadBase, "downloadCover")
                && HasCover(downloadBase.FilePath));
    }

    private static bool IsRequested(DownloadBase downloadBase, string contentName)
    {
        return downloadBase.NeedDownloadContent.TryGetValue(contentName, out var requested) && requested;
    }

    private static bool HasSubtitle(string basePath)
    {
        if (HasNonEmptyFile(basePath, [".srt"]))
        {
            return true;
        }

        var directory = Path.GetDirectoryName(basePath);
        var name = Path.GetFileName(basePath);
        if (string.IsNullOrWhiteSpace(directory)
            || string.IsNullOrWhiteSpace(name)
            || !Directory.Exists(directory))
        {
            return false;
        }

        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, $"{name}_*.srt", SearchOption.TopDirectoryOnly))
            {
                if (HasNonEmptyFile(path))
                {
                    return true;
                }
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }

    private static bool HasCover(string basePath)
    {
        foreach (var extension in CoverExtensions)
        {
            if (HasNonEmptyFile(basePath + extension)
                || HasNonEmptyFile(basePath + ".Cover" + extension))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasNonEmptyFile(string basePath, IReadOnlyList<string> extensions)
    {
        foreach (var extension in extensions)
        {
            if (HasNonEmptyFile(basePath + extension))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasNonEmptyFile(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length > 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
