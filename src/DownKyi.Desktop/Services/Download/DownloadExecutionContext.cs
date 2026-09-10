using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Core.Settings;
using DownKyi.Domain.Downloads;

namespace DownKyi.Services.Download;

internal sealed class DownloadExecutionContext
{
    private readonly Action<DownloadTaskId, CancellationToken> _ensureActive;

    public DownloadExecutionContext(
        DownloadTaskId taskId,
        DownloadExecutionInput input,
        PlayUrl? playUrl,
        Action<DownloadTaskId, CancellationToken> ensureActive)
    {
        TaskId = taskId ?? throw new ArgumentNullException(nameof(taskId));
        Input = input ?? throw new ArgumentNullException(nameof(input));
        PlayUrl = playUrl;
        _ensureActive = ensureActive ?? throw new ArgumentNullException(nameof(ensureActive));
    }

    public DownloadTaskId TaskId { get; }

    public DownloadExecutionInput Input { get; }

    public PlayUrl? PlayUrl { get; set; }

    public string? DownloadDirectory { get; set; }

    public DownloadMediaKind MediaKind { get; set; }

    public string? AudioFile { get; set; }

    public string? AudioTransferKey { get; set; }

    public string? VideoFile { get; set; }

    public string? VideoTransferKey { get; set; }

    public IReadOnlyList<DurlDownloadResult> DurlDownloads { get; set; } = [];

    public string? OutputMedia { get; set; }

    public bool MediaSucceeded { get; set; } = true;

    public string? DanmakuFile { get; set; }

    public IReadOnlyList<string>? SubtitleFiles { get; set; }

    public string? CoverFile { get; set; }

    public string? PageCoverFile { get; set; }

    public bool NeedsAudio => Input.RequestedContent.Audio;

    public bool NeedsVideo => Input.RequestedContent.Video;

    public bool NeedsMedia => NeedsAudio || NeedsVideo;

    public bool NeedsDanmaku => Input.RequestedContent.Danmaku;

    public bool NeedsSubtitle => Input.RequestedContent.Subtitle;

    public bool NeedsCover => Input.RequestedContent.Cover;

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

internal sealed record DownloadExecutionInput(
    DownloadTaskMetadata Metadata,
    DownloadContentSelection RequestedContent,
    string OutputBasePath,
    PlayStreamType StreamType,
    DownloadNfoRequest? NfoRequest,
    VideoApplicationSettings VideoSettings,
    DanmakuApplicationSettings DanmakuSettings,
    DownloadFinishedSort FinishedSort);

internal enum DownloadMediaKind
{
    None,
    Dash,
    Durl
}

internal sealed record DurlDownloadResult(
    PlayUrlDurl Durl,
    string FilePath,
    string TransferKey);
