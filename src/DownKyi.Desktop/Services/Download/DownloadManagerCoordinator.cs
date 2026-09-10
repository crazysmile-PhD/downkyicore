using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Desktop;
using DownKyi.Domain.Downloads;
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
    private static readonly ImmutableArray<string> VideoSuffixes = [".mp4", ".flv"];
    private static readonly ImmutableArray<string> AudioSuffixes = [".aac", ".mp3"];
    private static readonly ImmutableArray<string> CoverSuffixes = [".jpg", ".jpeg", ".png", ".webp"];
    private static readonly ImmutableArray<string> DanmakuSuffixes = [".ass"];
    private static readonly ImmutableArray<string> SubtitleSuffixes = [".srt"];

    private readonly DownloadTaskProjectionStore _storage;
    private readonly DownloadTaskStateWriter _stateWriter;
    private readonly IDownloadTaskQueue _taskQueue;
    private readonly IDownloadRuntimeAvailability _runtimeAvailability;
    private readonly DownloadTaskFileService _fileService;
    private readonly DownloadListState _downloadLists;
    private readonly IPlatformLauncher _platformLauncher;

    public DownloadManagerCoordinator(
        DownloadTaskProjectionStore storage,
        DownloadTaskStateWriter stateWriter,
        IDownloadTaskQueue taskQueue,
        IDownloadRuntimeAvailability runtimeAvailability,
        DownloadTaskFileService fileService,
        DownloadListState downloadLists,
        IPlatformLauncher platformLauncher)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _stateWriter = stateWriter ?? throw new ArgumentNullException(nameof(stateWriter));
        _taskQueue = taskQueue ?? throw new ArgumentNullException(nameof(taskQueue));
        _runtimeAvailability = runtimeAvailability
            ?? throw new ArgumentNullException(nameof(runtimeAvailability));
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
                await _stateWriter.PauseAsync(
                    GetTaskId(item),
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
                await ResumeAndEnqueueAsync(GetTaskId(item), cancellationToken).ConfigureAwait(true);
            }
        }
    }

    public async Task ToggleAsync(
        DownloadingItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var taskId = GetTaskId(item);
        if (item.Downloading.DownloadStatus is DownloadStatus.PauseStarted
            or DownloadStatus.Pause
            or DownloadStatus.DownloadFailed)
        {
            await ResumeAndEnqueueAsync(taskId, cancellationToken).ConfigureAwait(true);
            return;
        }

        if (item.Downloading.DownloadStatus is DownloadStatus.NotStarted
            or DownloadStatus.WaitForDownload
            or DownloadStatus.Downloading)
        {
            await _stateWriter.PauseAsync(taskId, cancellationToken).ConfigureAwait(true);
        }
    }

    public async Task DeleteAsync(
        DownloadingItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();

        var taskId = GetTaskId(item);
        if (_storage.GetRequiredSnapshot(taskId).Phase != DownloadPhase.Canceled)
        {
            await _stateWriter.CancelAsync(taskId, cancellationToken).ConfigureAwait(true);
        }

        await _taskQueue.CancelAsync(taskId).ConfigureAwait(true);
        await _fileService.CancelActiveDownloadAsync(item).ConfigureAwait(true);

        // Once physical deletion starts, finish the database/list transaction even if app shutdown is requested.
        var deletion = await _fileService
            .DeleteGeneratedFilesAsync(item, CancellationToken.None)
            .ConfigureAwait(true);
        if (!deletion.Succeeded)
        {
            throw new IOException("One or more generated download files could not be deleted.");
        }

        await _stateWriter.DeleteAsync(taskId, CancellationToken.None).ConfigureAwait(true);
        _downloadLists.RemoveDownloading(item);
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
        _downloadLists.ClearDownloaded();
    }

    public async Task RemoveDownloadedAsync(
        DownloadedItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        await _storage.RemoveDownloadedAsync(item, cancellationToken).ConfigureAwait(true);
        _downloadLists.RemoveDownloaded(item);
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

    private async Task ResumeAndEnqueueAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken)
    {
        _runtimeAvailability.EnsureAcceptingTasks();
        var resumed = await _stateWriter.ResumeAsync(taskId, cancellationToken).ConfigureAwait(true);
        try
        {
            await _taskQueue.EnqueueAsync(resumed.Id, CancellationToken.None).ConfigureAwait(true);
        }
        catch (DownloadRuntimeUnavailableException)
        {
            await _stateWriter.FailRuntimeUnavailableAsync(
                resumed.Id,
                CancellationToken.None).ConfigureAwait(true);
            throw;
        }
    }

    private static ImmutableArray<string> GetSelectedSuffixes(DownloadBase downloadBase)
    {
        var content = downloadBase.NeedDownloadContent;
        var suffixes = ImmutableArray.CreateBuilder<string>();
        if (content.Video)
        {
            suffixes.AddRange(VideoSuffixes);
        }

        if (content.Audio)
        {
            suffixes.AddRange(AudioSuffixes);
        }

        if (content.Cover)
        {
            suffixes.AddRange(CoverSuffixes);
        }

        if (content.Danmaku)
        {
            suffixes.AddRange(DanmakuSuffixes);
        }

        if (content.Subtitle)
        {
            suffixes.AddRange(SubtitleSuffixes);
        }

        return suffixes.ToImmutable();
    }

    private static DownloadTaskId GetTaskId(DownloadingItem item)
    {
        return new DownloadTaskId(item.DownloadBase.Id);
    }
}
