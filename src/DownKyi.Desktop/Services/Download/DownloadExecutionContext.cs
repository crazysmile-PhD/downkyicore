using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Core.Settings;
using DownKyi.Domain.Downloads;
using DownKyi.ViewModels.DownloadManager;

namespace DownKyi.Services.Download;

internal sealed class DownloadExecutionContext
{
    private readonly Action<DownloadTaskId, CancellationToken> _ensureActive;

    public DownloadExecutionContext(
        DownloadTaskId taskId,
        DownloadingItem downloading,
        ApplicationSettings settings,
        Action<DownloadTaskId, CancellationToken> ensureActive)
    {
        TaskId = taskId ?? throw new ArgumentNullException(nameof(taskId));
        Downloading = downloading ?? throw new ArgumentNullException(nameof(downloading));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _ensureActive = ensureActive ?? throw new ArgumentNullException(nameof(ensureActive));
    }

    public DownloadTaskId TaskId { get; }

    public DownloadingItem Downloading { get; }

    public ApplicationSettings Settings { get; }

    public string? DownloadDirectory { get; set; }

    public DownloadMediaKind MediaKind { get; set; }

    public string? AudioFile { get; set; }

    public string? VideoFile { get; set; }

    public IReadOnlyList<DurlDownloadResult> DurlDownloads { get; set; } = [];

    public string? OutputMedia { get; set; }

    public bool MediaSucceeded { get; set; } = true;

    public string? DanmakuFile { get; set; }

    public IReadOnlyList<string>? SubtitleFiles { get; set; }

    public string? CoverFile { get; set; }

    public string? PageCoverFile { get; set; }

    public bool NeedsAudio =>
        Downloading.DownloadBase.NeedDownloadContent["downloadAudio"];

    public bool NeedsVideo =>
        Downloading.DownloadBase.NeedDownloadContent["downloadVideo"];

    public bool NeedsMedia => NeedsAudio || NeedsVideo;

    public bool NeedsDanmaku =>
        Downloading.DownloadBase.NeedDownloadContent["downloadDanmaku"];

    public bool NeedsSubtitle =>
        Downloading.DownloadBase.NeedDownloadContent["downloadSubtitle"];

    public bool NeedsCover =>
        Downloading.DownloadBase.NeedDownloadContent["downloadCover"];

    public IReadOnlyList<string> GetMediaInputFiles()
    {
        return new[] { AudioFile, VideoFile }
            .Concat(DurlDownloads.Select(download => download.FilePath))
            .Where(file => !string.IsNullOrWhiteSpace(file))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public void EnsureActive(CancellationToken cancellationToken)
    {
        _ensureActive(TaskId, cancellationToken);
    }
}

internal enum DownloadMediaKind
{
    None,
    Dash,
    Durl
}

internal sealed record DurlDownloadResult(PlayUrlDurl Durl, string FilePath);
