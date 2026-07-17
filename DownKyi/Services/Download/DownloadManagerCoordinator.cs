using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Desktop;
using DownKyi.Models;
using DownKyi.ViewModels;
using DownKyi.ViewModels.DownloadManager;

namespace DownKyi.Services.Download;

internal enum DownloadArtifactOpenResult
{
    Opened,
    NotFound,
    OpenFailed
}

internal interface IDownloadManagerCoordinator
{
    Task PauseAllAsync(
        IEnumerable<DownloadingItem> items,
        CancellationToken cancellationToken = default);

    Task ResumeAllAsync(
        IEnumerable<DownloadingItem> items,
        CancellationToken cancellationToken = default);

    Task ToggleAsync(DownloadingItem item, CancellationToken cancellationToken = default);

    Task DeleteAsync(DownloadingItem item, CancellationToken cancellationToken = default);

    Task DeleteAllAsync(
        IEnumerable<DownloadingItem> items,
        CancellationToken cancellationToken = default);

    Task ClearDownloadedAsync(CancellationToken cancellationToken = default);

    Task RemoveDownloadedAsync(DownloadedItem item, CancellationToken cancellationToken = default);

    Task<DownloadArtifactOpenResult> OpenVideoAsync(
        DownloadedItem item,
        CancellationToken cancellationToken = default);

    Task<DownloadArtifactOpenResult> OpenFolderAsync(
        DownloadedItem item,
        CancellationToken cancellationToken = default);
}

internal sealed class DownloadManagerCoordinator : IDownloadManagerCoordinator
{
    private static readonly Dictionary<string, string[]> FileSuffixMap =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["downloadVideo"] = [".mp4", ".flv"],
            ["downloadAudio"] = [".aac", ".mp3"],
            ["downloadCover"] = [".jpg", ".jpeg", ".png", ".webp"],
            ["downloadDanmaku"] = [".ass"],
            ["downloadSubtitle"] = [".srt"]
        };

    private readonly DownloadStorageService _storage;
    private readonly DownloadTaskFileService _fileService;
    private readonly DownloadListState _downloadLists;
    private readonly IPlatformLauncher _platformLauncher;

    public DownloadManagerCoordinator(
        DownloadStorageService storage,
        DownloadTaskFileService fileService,
        DownloadListState downloadLists,
        IPlatformLauncher platformLauncher)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _fileService = fileService ?? throw new ArgumentNullException(nameof(fileService));
        _downloadLists = downloadLists ?? throw new ArgumentNullException(nameof(downloadLists));
        _platformLauncher = platformLauncher ?? throw new ArgumentNullException(nameof(platformLauncher));
    }

    public async Task PauseAllAsync(
        IEnumerable<DownloadingItem> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        foreach (var item in items.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Downloading.DownloadStatus is DownloadStatus.NotStarted
                or DownloadStatus.WaitForDownload
                or DownloadStatus.Downloading)
            {
                await ApplyAndPersistStatusAsync(
                    item,
                    DownloadStatus.Pause,
                    cancellationToken).ConfigureAwait(true);
            }
        }
    }

    public async Task ResumeAllAsync(
        IEnumerable<DownloadingItem> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        foreach (var item in items.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Downloading.DownloadStatus is DownloadStatus.NotStarted
                or DownloadStatus.WaitForDownload
                or DownloadStatus.PauseStarted
                or DownloadStatus.Pause
                or DownloadStatus.DownloadFailed)
            {
                await ApplyAndPersistStatusAsync(
                    item,
                    DownloadStatus.WaitForDownload,
                    cancellationToken).ConfigureAwait(true);
            }
        }
    }

    public Task ToggleAsync(
        DownloadingItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var target = item.Downloading.DownloadStatus switch
        {
            DownloadStatus.NotStarted or DownloadStatus.WaitForDownload => DownloadStatus.PauseStarted,
            DownloadStatus.PauseStarted or DownloadStatus.Pause or DownloadStatus.DownloadFailed =>
                DownloadStatus.WaitForDownload,
            DownloadStatus.Downloading => DownloadStatus.Pause,
            _ => item.Downloading.DownloadStatus
        };

        return target == item.Downloading.DownloadStatus
            ? Task.CompletedTask
            : ApplyAndPersistStatusAsync(item, target, cancellationToken);
    }

    public async Task DeleteAsync(
        DownloadingItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();

        await ApplyAndPersistStatusAsync(item, DownloadStatus.Pause, cancellationToken)
            .ConfigureAwait(true);
        await _fileService.CancelActiveDownloadAsync(item).ConfigureAwait(true);

        // Once physical deletion starts, finish the database/list transaction even if app shutdown is requested.
        var deletion = await _fileService
            .DeleteGeneratedFilesAsync(item, CancellationToken.None)
            .ConfigureAwait(true);
        if (!deletion.Succeeded)
        {
            throw new IOException("One or more generated download files could not be deleted.");
        }

        await _storage
            .RemoveDownloadingAsync(item, cascadeRemove: true, CancellationToken.None)
            .ConfigureAwait(true);
        _downloadLists.Downloading.Remove(item);
    }

    public async Task DeleteAllAsync(
        IEnumerable<DownloadingItem> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        foreach (var item in items.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DeleteAsync(item, cancellationToken).ConfigureAwait(true);
        }
    }

    public async Task ClearDownloadedAsync(CancellationToken cancellationToken = default)
    {
        await _storage.ClearDownloadedAsync(cancellationToken).ConfigureAwait(true);
        _downloadLists.Downloaded.Clear();
    }

    public async Task RemoveDownloadedAsync(
        DownloadedItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        await _storage.RemoveDownloadedAsync(item, cancellationToken).ConfigureAwait(true);
        _downloadLists.Downloaded.Remove(item);
    }

    public Task<DownloadArtifactOpenResult> OpenVideoAsync(
        DownloadedItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        return OpenFirstFileAsync(item.DownloadBase?.FilePath, [".mp4", ".flv"], cancellationToken);
    }

    public async Task<DownloadArtifactOpenResult> OpenFolderAsync(
        DownloadedItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();
        var downloadBase = item.DownloadBase;
        if (downloadBase == null || string.IsNullOrWhiteSpace(downloadBase.FilePath))
        {
            return DownloadArtifactOpenResult.NotFound;
        }

        foreach (var suffix in GetSelectedSuffixes(downloadBase))
        {
            var candidate = downloadBase.FilePath + suffix;
            if (!File.Exists(candidate))
            {
                continue;
            }

            var directory = Path.GetDirectoryName(Path.GetFullPath(candidate));
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            return await _platformLauncher.OpenFolderAsync(directory, cancellationToken)
                .ConfigureAwait(true)
                ? DownloadArtifactOpenResult.Opened
                : DownloadArtifactOpenResult.OpenFailed;
        }

        return DownloadArtifactOpenResult.NotFound;
    }

    private async Task ApplyAndPersistStatusAsync(
        DownloadingItem item,
        DownloadStatus target,
        CancellationToken cancellationToken)
    {
        var previousStatus = item.Downloading.DownloadStatus;
        var previousTitle = item.DownloadStatusTitle;
        item.ApplyControlStatus(target);
        try
        {
            await _storage.UpdateDownloadingAsync(item, cancellationToken).ConfigureAwait(true);
        }
        catch
        {
            item.RestoreControlStatus(previousStatus, previousTitle);
            throw;
        }
    }

    private async Task<DownloadArtifactOpenResult> OpenFirstFileAsync(
        string? basePath,
        IEnumerable<string> suffixes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(basePath))
        {
            return DownloadArtifactOpenResult.NotFound;
        }

        foreach (var suffix in suffixes)
        {
            var candidate = basePath + suffix;
            if (!File.Exists(candidate))
            {
                continue;
            }

            return await _platformLauncher.OpenFileAsync(Path.GetFullPath(candidate), cancellationToken)
                .ConfigureAwait(true)
                ? DownloadArtifactOpenResult.Opened
                : DownloadArtifactOpenResult.OpenFailed;
        }

        return DownloadArtifactOpenResult.NotFound;
    }

    private static IEnumerable<string> GetSelectedSuffixes(DownloadBase downloadBase)
    {
        return downloadBase.NeedDownloadContent
            .Where(item => item.Value && FileSuffixMap.ContainsKey(item.Key))
            .SelectMany(item => FileSuffixMap[item.Key]);
    }
}
