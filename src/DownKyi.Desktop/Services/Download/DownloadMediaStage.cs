using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Diagnostics;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Core.Settings;
using DownKyi.Domain.Results;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

internal sealed class DownloadMediaStage : IDownloadPipelineStage
{
    private readonly DownloadTaskProjectionStore _projectionStore;
    private readonly DownloadTaskStateWriter _stateWriter;
    private readonly DownloadTransferCoordinator _transferCoordinator;
    private readonly DownloadPlaybackResolver _playbackResolver;
    private readonly ILogger _logger;

    public DownloadMediaStage(
        DownloadTaskProjectionStore projectionStore,
        DownloadTaskStateWriter stateWriter,
        DownloadTransferCoordinator transferCoordinator,
        DownloadPlaybackResolver playbackResolver,
        ILogger logger)
    {
        _projectionStore = projectionStore
            ?? throw new ArgumentNullException(nameof(projectionStore));
        _stateWriter = stateWriter ?? throw new ArgumentNullException(nameof(stateWriter));
        _transferCoordinator = transferCoordinator
            ?? throw new ArgumentNullException(nameof(transferCoordinator));
        _playbackResolver = playbackResolver
            ?? throw new ArgumentNullException(nameof(playbackResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string Name => nameof(DownloadMediaStage);

    public async Task<OperationResult<DownloadStageResult>> ExecuteAsync(
        DownloadExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.EnsureActive(cancellationToken);
        var playUrl = context.Downloading.PlayUrl;
        context.MediaKind = DetectMediaKind(playUrl);
        if (context.MediaKind == DownloadMediaKind.Dash)
        {
            return await DownloadDashAsync(context, cancellationToken).ConfigureAwait(true);
        }

        if (context.MediaKind == DownloadMediaKind.Durl)
        {
            return await DownloadDurlsAsync(
                context,
                playUrl.Durl,
                cancellationToken).ConfigureAwait(true);
        }

        return DownloadStageResult.Failure(
            "download.media.missing",
            "Playback data does not contain a supported media stream.");
    }

    internal static DownloadMediaKind DetectMediaKind(PlayUrl? playUrl)
    {
        if (playUrl?.Dash is { } dash &&
            (dash.Video.Count > 0 || dash.Audio.Count > 0))
        {
            return DownloadMediaKind.Dash;
        }

        return playUrl?.Durl.Count > 0
            ? DownloadMediaKind.Durl
            : DownloadMediaKind.None;
    }

    internal static PlayUrlDashVideo? CreateDurlDownloadDescriptor(
        IEnumerable<PlayUrlDurl> durls)
    {
        ArgumentNullException.ThrowIfNull(durls);
        var durl = durls.OrderBy(item => item.Order).FirstOrDefault();
        return durl == null
            ? null
            : new PlayUrlDashVideo
            {
                BackupUrl = durl.BackupUrl,
                BaseAddress = durl.SourceAddress,
                Codecs = "durl",
                Id = durl.Order,
                ExpectedSize = durl.Size
            };
    }

    private async Task<OperationResult<DownloadStageResult>> DownloadDashAsync(
        DownloadExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (context.NeedsAudio)
        {
            DownloadActivityPresenter.ShowDownloadingAudio(context.Downloading);
            var result = await DownloadMediaFileAsync(
                context,
                SelectAudio(context),
                playUrl => SelectAudio(context, playUrl),
                cancellationToken).ConfigureAwait(true);
            if (!result.TryGetValue(out var audioFile))
            {
                return DownloadStageResult.Failure(
                    result.Error?.Code ?? "download.media.audio",
                    result.Error?.Message ?? "Audio transfer failed.");
            }

            context.AudioFile = audioFile;
        }

        context.EnsureActive(cancellationToken);
        if (context.NeedsVideo)
        {
            DownloadActivityPresenter.ShowDownloadingVideo(context.Downloading);
            var result = await DownloadMediaFileAsync(
                context,
                SelectVideo(context),
                playUrl => SelectVideo(context, playUrl),
                cancellationToken).ConfigureAwait(true);
            if (!result.TryGetValue(out var videoFile))
            {
                return DownloadStageResult.Failure(
                    result.Error?.Code ?? "download.media.video",
                    result.Error?.Message ?? "Video transfer failed.");
            }

            context.VideoFile = videoFile;
        }

        context.EnsureActive(cancellationToken);
        return DownloadStageResult.Success(Name);
    }

    private async Task<OperationResult<DownloadStageResult>> DownloadDurlsAsync(
        DownloadExecutionContext context,
        IEnumerable<PlayUrlDurl> source,
        CancellationToken cancellationToken)
    {
        if (!context.NeedsMedia)
        {
            context.EnsureActive(cancellationToken);
            return DownloadStageResult.Success(Name);
        }

        DownloadActivityPresenter.ShowDownloadingVideo(context.Downloading);
        var downloads = source
            .OrderBy(durl => durl.Order)
            .Select(durl => new PendingDurlDownload(durl))
            .ToArray();

        foreach (var download in downloads)
        {
            var result = await DownloadMediaFileAsync(
                context,
                CreateDurlDownloadDescriptor([download.Durl]),
                playUrl => SelectDurl(playUrl, download.Durl.Order),
                cancellationToken).ConfigureAwait(true);
            if (!result.TryGetValue(out var filePath))
            {
                return DownloadStageResult.Failure(
                    result.Error?.Code ?? "download.media.durl",
                    result.Error?.Message ?? "A segmented media transfer failed.");
            }

            download.FilePath = filePath;
        }

        context.DurlDownloads = downloads
            .Select(download => new DurlDownloadResult(
                download.Durl,
                GetCompletedFilePath(download)))
            .ToArray();
        context.EnsureActive(cancellationToken);
        return DownloadStageResult.Success(Name);
    }

    private static string GetCompletedFilePath(PendingDurlDownload download)
    {
        return download.FilePath
               ?? throw new InvalidOperationException(
                   "A completed DURL transfer must have a file path.");
    }

    private async Task<OperationResult<string>> DownloadMediaFileAsync(
        DownloadExecutionContext context,
        PlayUrlDashVideo? media,
        Func<PlayUrl, PlayUrlDashVideo?> selectRefreshedMedia,
        CancellationToken cancellationToken)
    {
        if (media == null)
        {
            return OperationResult.Failure<string>(OperationError.Unexpected(
                "download.media.descriptor",
                "The selected media stream is unavailable."));
        }

        context.EnsureActive(cancellationToken);
        var urls = CreateAddresses(media);
        if (urls.Count == 0)
        {
            return OperationResult.Failure<string>(OperationError.Unexpected(
                "download.media.url",
                "The selected media stream has no usable address."));
        }

        var path = context.DownloadDirectory;
        if (string.IsNullOrWhiteSpace(path))
        {
            return OperationResult.Failure<string>(OperationError.Unexpected(
                "download.media.directory",
                "The download directory is unavailable."));
        }

        var fileName = Guid.NewGuid().ToString("N");
        var key = DownloadTransferKey.Create(media.Id, media.Codecs);
        var snapshot = _projectionStore.GetRequiredSnapshot(context.TaskId);
        if (snapshot.Plan.TransferFiles.TryGetValue(key, out var existingFileName))
        {
            fileName = existingFileName;
            var cachedFile = Path.Combine(path, fileName);
            if (snapshot.Transfer.CompletedFileKeys.Contains(key, StringComparer.Ordinal) &&
                IsDownloadedMediaFileUsable(cachedFile, media.ExpectedSize))
            {
                return OperationResult.Success(cachedFile);
            }

            if (snapshot.Transfer.CompletedFileKeys.Contains(key, StringComparer.Ordinal))
            {
                DownloadTransferFileCleanup.DeleteInvalidArtifacts(cachedFile, _logger);
                await _stateWriter.InvalidateCompletedFileAsync(
                    context.TaskId,
                    key,
                    cancellationToken).ConfigureAwait(true);
            }
        }
        else
        {
            await _stateWriter.RecordTransferFileAsync(
                context.TaskId,
                key,
                fileName,
                cancellationToken).ConfigureAwait(true);
        }

        NormalizeTransferSchemes(
            urls,
            context.Settings.Network.UseSsl == AllowStatus.Yes);
        var targetFile = Path.Combine(path, fileName);
        var transferRequest = DownloadTransferRequestFactory.Create(
                context.TaskId,
                urls,
                path,
                fileName,
                media.ExpectedSize,
                _projectionStore,
                _stateWriter,
                () => context.EnsureActive(cancellationToken),
                cancellationToken);
        var result = await _transferCoordinator.TransferAsync(
            transferRequest,
            token => RefreshAddressesAsync(
                context,
                selectRefreshedMedia,
                token),
            cancellationToken).ConfigureAwait(true);
        if (result.Outcome == DownloadTransferOutcome.Succeeded)
        {
            if (!IsDownloadedMediaFileUsable(targetFile, media.ExpectedSize))
            {
                DownloadTransferFileCleanup.DeleteInvalidArtifacts(targetFile, _logger);
                await _stateWriter.SetBackendIdentityAsync(
                    context.TaskId,
                    null,
                    cancellationToken).ConfigureAwait(true);
                return OperationResult.Failure<string>(OperationError.Unexpected(
                    "download.transfer.invalid-media",
                    "The transfer completed with an invalid media file."));
            }

            await _stateWriter.CompleteTransferFileAsync(
                context.TaskId,
                key,
                cancellationToken).ConfigureAwait(true);
            return OperationResult.Success(targetFile);
        }

        if (result.Outcome == DownloadTransferOutcome.Paused)
        {
            throw new OperationCanceledException("Download was paused.");
        }

        return OperationResult.Failure<string>(OperationError.Unexpected(
            result.ErrorCode,
            "Media transfer did not produce a valid file."));
    }

    private static PlayUrlDashVideo? SelectAudio(DownloadExecutionContext context)
    {
        return SelectAudio(context, context.Downloading.PlayUrl);
    }

    private static PlayUrlDashVideo? SelectAudio(
        DownloadExecutionContext context,
        PlayUrl? playUrl)
    {
        var downloading = context.Downloading;
        var dash = playUrl?.Dash;
        if (dash?.Audio is not { Count: > 0 } audio)
        {
            return null;
        }

        var selected = audio.FirstOrDefault(item => item.Id == downloading.AudioCodec.Id);
        if (downloading.AudioCodec.Id == 30250 &&
            dash.Dolby?.Audio is { Count: > 0 } dolbyAudio)
        {
            selected = dolbyAudio[0];
        }

        if (downloading.AudioCodec.Id == 30251 && dash.Flac?.Audio is { } flacAudio)
        {
            selected = flacAudio;
        }

        return selected;
    }

    internal static PlayUrlDashVideo? SelectVideo(DownloadExecutionContext context)
    {
        return SelectVideo(context, context.Downloading.PlayUrl);
    }

    private static PlayUrlDashVideo? SelectVideo(
        DownloadExecutionContext context,
        PlayUrl? playUrl)
    {
        var downloading = context.Downloading;
        var video = playUrl?.Dash?.Video?.FirstOrDefault(item =>
        {
            var codec = PlaybackQualityCatalog.GetCodecIds().FirstOrDefault(candidate =>
                candidate.Id == item.CodecId);
            return item.Id == downloading.Resolution.Id &&
                   codec?.Name == downloading.VideoCodecName;
        });
        if (video == null)
        {
            return null;
        }

        return video;
    }

    private async Task<IReadOnlyList<string>> RefreshAddressesAsync(
        DownloadExecutionContext context,
        Func<PlayUrl, PlayUrlDashVideo?> selectRefreshedMedia,
        CancellationToken cancellationToken)
    {
        var playUrl = await _playbackResolver.ResolveAsync(
            context,
            cancellationToken).ConfigureAwait(true);
        if (playUrl == null)
        {
            return [];
        }

        context.Downloading.PlayUrl = playUrl;
        var media = selectRefreshedMedia(playUrl);
        if (media == null)
        {
            return [];
        }

        var addresses = CreateAddresses(media);
        NormalizeTransferSchemes(
            addresses,
            context.Settings.Network.UseSsl == AllowStatus.Yes);
        return addresses;
    }

    private static PlayUrlDashVideo? SelectDurl(PlayUrl playUrl, int order)
    {
        var durl = playUrl.Durl.FirstOrDefault(candidate => candidate.Order == order);
        return durl == null ? null : CreateDurlDownloadDescriptor([durl]);
    }

    private static List<string> CreateAddresses(PlayUrlDashVideo media)
    {
        var addresses = new List<string>();
        if (!string.IsNullOrWhiteSpace(media.BaseAddress))
        {
            addresses.Add(media.BaseAddress);
        }

        addresses.AddRange(media.BackupUrl.Where(url => !string.IsNullOrWhiteSpace(url)));
        return addresses;
    }

    private bool IsDownloadedMediaFileUsable(
        string? file,
        long expectedBytes = 0)
    {
        var result = DownloadFileIntegrity.Check(file, expectedBytes);
        if (!result.IsUsable)
        {
            _logger.LogInformationMessage(
                result.Reason ?? "Downloaded media file is not usable.");
        }

        return result.IsUsable;
    }

    private static void NormalizeTransferSchemes(List<string> urls, bool useSsl)
    {
        for (var index = 0; index < urls.Count; index++)
        {
            var url = urls[index];
            if (useSsl && url.StartsWith("http://", StringComparison.Ordinal))
            {
                urls[index] = "https://" + url["http://".Length..];
            }
            else if (!useSsl && url.StartsWith("https://", StringComparison.Ordinal))
            {
                urls[index] = "http://" + url["https://".Length..];
            }
        }
    }

    private sealed class PendingDurlDownload(PlayUrlDurl durl)
    {
        public PlayUrlDurl Durl { get; } = durl;

        public string? FilePath { get; set; }
    }
}
