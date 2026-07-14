using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Core.Aria2cNet.Client;
using DownKyi.Core.Logging;
using DownKyi.Models;
using DownKyi.ViewModels.DownloadManager;

namespace DownKyi.Services.Download;

internal static class DownloadTaskFileService
{
    private const string Tag = nameof(DownloadTaskFileService);
    private static readonly string[] MediaExtensions = { ".mp4", ".aac", ".mp3", ".flac" };
    private static readonly string[] TextExtensions = { ".ass", ".srt", ".nfo" };
    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".avif", ".gif" };
    private static readonly string[] TempExtensions = { "", ".aria2", ".download" };

    public static async Task CancelActiveDownloadAsync(DownloadingItem downloading)
    {
        ArgumentNullException.ThrowIfNull(downloading);

        downloading.Downloading.DownloadStatus = DownloadStatus.Pause;

        try
        {
            downloading.DownloadService?.CancelAsync();
        }
        catch (InvalidOperationException e)
        {
            LogManager.Debug(Tag, $"Cancel builtin downloader failed: {e.Message}");
        }
        finally
        {
            downloading.DownloadService = null;
        }

        var gid = downloading.Downloading.Gid;
        if (string.IsNullOrWhiteSpace(gid))
        {
            return;
        }

        var removed = false;
        try
        {
            await AriaClient.RemoveAsync(gid).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            await AriaClient.RemoveDownloadResultAsync(gid).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            removed = true;
        }
        catch (TimeoutException e)
        {
            LogManager.Debug(Tag, $"Cancel aria downloader failed: {e.Message}");
        }
        catch (HttpRequestException e)
        {
            LogManager.Debug(Tag, $"Cancel aria downloader failed: {e.Message}");
        }
        finally
        {
            if (removed)
            {
                downloading.Downloading.Gid = null;
            }
        }
    }

    public static Task DeleteGeneratedFilesAsync(DownloadingItem downloading, CancellationToken cancellationToken = default)
    {
        var files = GetGeneratedFiles(downloading);
        return DeleteFilesAsync(files, cancellationToken);
    }

    internal static Task DeleteFilesAsync(
        IEnumerable<string> files,
        CancellationToken cancellationToken = default)
    {
        // File.Delete has no async API and can block on network drives or antivirus scans.
        return Task.Run(() => DeleteFilesCoreAsync(files, cancellationToken), cancellationToken);
    }

    private static async Task DeleteFilesCoreAsync(
        IEnumerable<string> files,
        CancellationToken cancellationToken)
    {
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await TryDeleteFileAsync(file, cancellationToken).ConfigureAwait(false);
        }
    }

    public static IReadOnlyCollection<string> GetGeneratedFiles(DownloadingItem downloading)
    {
        ArgumentNullException.ThrowIfNull(downloading);

        return GetGeneratedFiles(
            downloading.DownloadBase?.FilePath,
            downloading.Downloading.DownloadFiles?.Values);
    }

    internal static IReadOnlyCollection<string> GetGeneratedFiles(
        string? filePath,
        IEnumerable<string>? downloadFiles)
    {
        var files = OperatingSystem.IsWindows()
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.Ordinal);

        var basePath = NormalizePath(filePath);
        var directory = Path.GetDirectoryName(basePath);

        if (!string.IsNullOrWhiteSpace(directory))
        {
            foreach (var fileName in downloadFiles ?? Enumerable.Empty<string>())
            {
                AddWithTempFiles(files, ResolveDownloadFile(directory, fileName));
            }
        }

        AddKnownOutputFiles(files, basePath);
        AddSubtitleVariants(files, basePath);

        return files
            .Where(file => !string.IsNullOrWhiteSpace(file))
            .Select(Path.GetFullPath)
            .ToList();
    }

    private static void AddKnownOutputFiles(ISet<string> files, string basePath)
    {
        if (string.IsNullOrWhiteSpace(basePath))
        {
            return;
        }

        foreach (var extension in MediaExtensions.Concat(TextExtensions))
        {
            AddWithTempFiles(files, basePath + extension);
        }

        foreach (var extension in ImageExtensions)
        {
            AddWithTempFiles(files, basePath + extension);
            AddWithTempFiles(files, basePath + ".Cover" + extension);
        }
    }

    private static void AddSubtitleVariants(ISet<string> files, string basePath)
    {
        var directory = Path.GetDirectoryName(basePath);
        var name = Path.GetFileName(basePath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(name) || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            foreach (var subtitle in Directory.EnumerateFiles(directory, $"{name}_*.srt", SearchOption.TopDirectoryOnly))
            {
                AddWithTempFiles(files, subtitle);
            }
        }
        catch (IOException e)
        {
            LogManager.Debug(Tag, $"Enumerate subtitle variants failed: {e.Message}");
        }
        catch (UnauthorizedAccessException e)
        {
            LogManager.Debug(Tag, $"Enumerate subtitle variants was denied: {e.Message}");
        }
    }

    private static void AddWithTempFiles(ISet<string> files, string file)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            return;
        }

        foreach (var extension in TempExtensions)
        {
            files.Add(file + extension);
        }
    }

    private static string ResolveDownloadFile(string directory, string fileName)
    {
        var normalized = NormalizePath(fileName);
        return Path.IsPathRooted(normalized)
            ? normalized
            : Path.Combine(directory, normalized);
    }

    private static string NormalizePath(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
    }

    private static async Task TryDeleteFileAsync(string file, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }

                return;
            }
            catch (IOException e) when (attempt < 4)
            {
                LogManager.Debug(Tag, $"Delete generated file retry {attempt + 1}: {e.Message}");
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException e) when (attempt < 4)
            {
                LogManager.Debug(Tag, $"Delete generated file retry {attempt + 1}: {e.Message}");
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
            }
            catch (IOException e)
            {
                LogManager.Error(Tag, e);
                return;
            }
            catch (UnauthorizedAccessException e)
            {
                LogManager.Error(Tag, e);
                return;
            }
        }
    }
}
