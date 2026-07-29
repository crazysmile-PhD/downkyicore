using System;
using System.IO;
using DownKyi.Models;

namespace DownKyi.Services.Download;

internal static class CompletedMediaOutput
{
    private static readonly string[] VideoExtensions = [".mp4", ".flv"];
    private static readonly string[] AudioExtensions = [".aac", ".mp3", ".flac"];

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

        return true;
    }

    private static bool IsRequested(DownloadBase downloadBase, string contentName)
    {
        return downloadBase.NeedDownloadContent.TryGetValue(contentName, out var requested) && requested;
    }

    private static bool HasNonEmptyFile(string basePath, string[] extensions)
    {
        foreach (var extension in extensions)
        {
            var path = basePath + extension;
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > 0)
                {
                    return true;
                }
            }
            catch (IOException)
            {
                // The output may be moved between the existence and length checks.
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                // An inaccessible output cannot safely satisfy duplicate detection.
                return false;
            }
        }

        return false;
    }
}
