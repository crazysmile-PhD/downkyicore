using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Diagnostics;
using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

internal sealed class DownloadMediaStage : IDownloadPipelineStage
{
    private readonly DownloadTaskProjectionStore _projectionStore;
    private readonly DownloadTaskStateWriter _stateWriter;
    private readonly DownloadTransferCoordinator _transferCoordinator;
    private readonly DownloadPlaybackResolver _playbackResolver;
    private readonly DownloadActivityPresenter _presenter;
    private readonly ILogger _logger;

    public DownloadMediaStage(
        DownloadTaskProjectionStore projectionStore,
        DownloadTaskStateWriter stateWriter,
        DownloadTransferCoordinator transferCoordinator,
        DownloadPlaybackResolver playbackResolver,
        DownloadActivityPresenter presenter,
        ILogger logger)
    {
        _projectionStore = projectionStore
            ?? throw new ArgumentNullException(nameof(projectionStore));
        _stateWriter = stateWriter ?? throw new ArgumentNullException(nameof(stateWriter));
        _transferCoordinator = transferCoordinator
            ?? throw new ArgumentNullException(nameof(transferCoordinator));
        _playbackResolver = playbackResolver
            ?? throw new ArgumentNullException(nameof(playbackResolver));
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string Name => nameof(DownloadMediaStage);

    public async Task<OperationResult<DownloadStageResult>> ExecuteAsync(
        DownloadExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.EnsureActive(cancellationToken);
        var playUrl = context.PlayUrl;
        var contractFailure = DownloadMediaContract.Validate(context, playUrl);
        if (contractFailure != null)
        {
            return OperationResult.Failure<DownloadStageResult>(contractFailure);
        }

        if (!context.NeedsMedia)
        {
            context.MediaKind = DownloadMediaKind.None;
            return DownloadStageResult.Success(Name);
        }

        context.MediaKind = context.Input.RequestedContent.MediaKind!.Value;
        if (context.TryReuseStagedMedia())
        {
            return DownloadStageResult.Success(Name);
        }

        if (context.MediaKind == DownloadMediaKind.Dash)
        {
            return await DownloadDashAsync(context, cancellationToken).ConfigureAwait(true);
        }

        if (context.MediaKind == DownloadMediaKind.Durl)
        {
            return await DownloadDurlsAsync(
                context,
                playUrl!.Durl,
                cancellationToken).ConfigureAwait(true);
        }

        return DownloadStageResult.Failure(
            "download.media.missing",
            "Playback data does not contain a supported media stream.");
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
                Source = durl.Source,
                Codecs = "durl",
                Id = durl.Order,
                ExpectedSize = durl.Size
            };
    }

    private async Task<OperationResult<DownloadStageResult>> DownloadDashAsync(
        DownloadExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (context.NeedsPendingAudio)
        {
            var audio = SelectAudio(context);
            _presenter.ShowDownloadingAudio(context);
            var result = await DownloadMediaFileAsync(
                context,
                audio,
                playUrl => SelectAudio(context, playUrl),
                cancellationToken).ConfigureAwait(true);
            if (!result.TryGetValue(out var audioTransfer))
            {
                return DownloadStageResult.Failure(
                    result.Error?.Code ?? "download.media.audio",
                    result.Error?.Message ?? "Audio transfer failed.");
            }

            context.AudioFile = audioTransfer.FilePath;
            context.AudioTransferKey = audioTransfer.Key;
        }

        context.EnsureActive(cancellationToken);
        if (context.NeedsPendingVideo)
        {
            _presenter.ShowDownloadingVideo(context);
            var result = await DownloadMediaFileAsync(
                context,
                SelectVideo(context),
                playUrl => SelectVideo(context, playUrl),
                cancellationToken).ConfigureAwait(true);
            if (!result.TryGetValue(out var videoTransfer))
            {
                return DownloadStageResult.Failure(
                    result.Error?.Code ?? "download.media.video",
                    result.Error?.Message ?? "Video transfer failed.");
            }

            context.VideoFile = videoTransfer.FilePath;
            context.VideoTransferKey = videoTransfer.Key;
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

        if (!TryValidateAndOrderDurls(source, out var orderedDurls))
        {
            return DownloadStageResult.Failure(
                "download.media.durl-manifest",
                "The segmented media manifest is invalid.");
        }

        _presenter.ShowDownloadingVideo(context);
        var downloads = orderedDurls
            .Select(durl => new PendingDurlDownload(durl))
            .ToArray();

        foreach (var download in downloads)
        {
            var result = await DownloadMediaFileAsync(
                context,
                CreateDurlDownloadDescriptor([download.Durl]),
                playUrl => SelectDurl(playUrl, download.Durl.Order),
                cancellationToken).ConfigureAwait(true);
            if (!result.TryGetValue(out var transfer))
            {
                return DownloadStageResult.Failure(
                    result.Error?.Code ?? "download.media.durl",
                    result.Error?.Message ?? "A segmented media transfer failed.");
            }

            download.Transfer = transfer;
        }

        context.DurlDownloads = downloads
            .Select(download => new DurlDownloadResult(
                download.Durl,
                GetCompletedTransfer(download).FilePath,
                GetCompletedTransfer(download).Key))
            .ToArray();
        context.EnsureActive(cancellationToken);
        return DownloadStageResult.Success(Name);
    }

    private static DownloadedMediaTransfer GetCompletedTransfer(PendingDurlDownload download) =>
        download.Transfer ?? throw new InvalidOperationException(
            "A completed DURL transfer must have a transfer reference.");

    private async Task<OperationResult<DownloadedMediaTransfer>> DownloadMediaFileAsync(
        DownloadExecutionContext context,
        PlayUrlDashVideo? media,
        Func<PlayUrl, PlayUrlDashVideo?> selectRefreshedMedia,
        CancellationToken cancellationToken)
    {
        if (media == null)
        {
            return OperationResult.Failure<DownloadedMediaTransfer>(OperationError.Unexpected(
                "download.media.descriptor",
                "The selected media stream is unavailable."));
        }

        context.EnsureActive(cancellationToken);
        var urls = CreateAddresses(media);
        if (urls.Count == 0)
        {
            return OperationResult.Failure<DownloadedMediaTransfer>(OperationError.Unexpected(
                "download.media.url",
                "The selected media stream has no usable address."));
        }

        var path = context.DownloadDirectory;
        if (string.IsNullOrWhiteSpace(path))
        {
            return OperationResult.Failure<DownloadedMediaTransfer>(OperationError.Unexpected(
                "download.media.directory",
                "The download directory is unavailable."));
        }

        var fileName = Guid.NewGuid().ToString("N");
        var key = DownloadTransferKey.Create(media.Id, media.Codecs);
        var snapshot = _projectionStore.GetRequiredSnapshot(context.TaskId);
        if (snapshot.Plan.TransferFiles.TryGetValue(key, out var existingFileName))
        {
            if (existingFileName != Path.GetFileName(existingFileName))
            {
                await _stateWriter.RecordTransferFileAsync(
                    context.TaskId, key, fileName, cancellationToken).ConfigureAwait(true);
            }
            else
            {
                fileName = existingFileName;
            }

            var cachedFile = Path.Combine(path, fileName);
            if (snapshot.Transfer.CompletedFileKeys.Contains(key, StringComparer.Ordinal) &&
                IsDownloadedMediaFileUsable(cachedFile, media.ExpectedSize))
            {
                return OperationResult.Success(new DownloadedMediaTransfer(key, cachedFile));
            }

            if (snapshot.Transfer.CompletedFileKeys.Contains(key, StringComparer.Ordinal))
            {
                if (!DownloadTransferFileCleanup.DeleteInvalidArtifacts(
                        cachedFile, context.StagingDirectory, _logger).Succeeded)
                {
                    return CleanupFailure();
                }

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

        RequireSecureTransferSchemes(urls);
        var targetFile = Path.Combine(path, fileName);
        var transferRequest = DownloadTransferRequestFactory.Create(
                context.TaskId,
                urls,
                path,
                context.StagingDirectory,
                fileName,
                media.ExpectedSize,
                _projectionStore,
                _stateWriter,
                () => context.EnsureActive(cancellationToken),
                cancellationToken);
        OperationError? refreshFailure = null;
        var result = await _transferCoordinator.TransferAsync(
            transferRequest,
            async token =>
            {
                var refresh = await RefreshAddressesAsync(
                    context,
                    selectRefreshedMedia,
                    token).ConfigureAwait(true);
                if (refresh.TryGetValue(out var refreshedAddresses))
                {
                    return refreshedAddresses;
                }

                refreshFailure = refresh.Error;
                return [];
            },
            cancellationToken).ConfigureAwait(true);
        if (refreshFailure != null)
        {
            return OperationResult.Failure<DownloadedMediaTransfer>(refreshFailure);
        }

        if (result.Outcome == DownloadTransferOutcome.Succeeded)
        {
            if (!IsDownloadedMediaFileUsable(targetFile, media.ExpectedSize))
            {
                if (!DownloadTransferFileCleanup.DeleteInvalidArtifacts(
                        targetFile, context.StagingDirectory, _logger).Succeeded)
                {
                    return CleanupFailure();
                }

                await _stateWriter.SetBackendIdentityAsync(
                    context.TaskId,
                    null,
                    cancellationToken).ConfigureAwait(true);
                return OperationResult.Failure<DownloadedMediaTransfer>(OperationError.Unexpected(
                    "download.transfer.invalid-media",
                    "The transfer completed with an invalid media file."));
            }

            await _stateWriter.CompleteTransferFileAsync(
                context.TaskId,
                key,
                cancellationToken).ConfigureAwait(true);
            return OperationResult.Success(new DownloadedMediaTransfer(key, targetFile));
        }

        if (result.Outcome == DownloadTransferOutcome.Paused)
        {
            throw new OperationCanceledException("Download was paused.");
        }

        return OperationResult.Failure<DownloadedMediaTransfer>(OperationError.Unexpected(
            result.ErrorCode,
            "Media transfer did not produce a valid file."));
    }

    private static OperationResult<DownloadedMediaTransfer> CleanupFailure()
    {
        return OperationResult.Failure<DownloadedMediaTransfer>(OperationError.Unexpected(
            "download.transfer.cleanup",
            "Invalid transfer artifacts could not be removed safely."));
    }

    internal static PlayUrlDashVideo? SelectAudio(DownloadExecutionContext context) =>
        DownloadMediaContract.SelectAudio(context, context.PlayUrl);

    private static PlayUrlDashVideo? SelectAudio(
        DownloadExecutionContext context,
        PlayUrl? playUrl) =>
        DownloadMediaContract.SelectAudio(context, playUrl);

    internal static PlayUrlDashVideo? SelectVideo(DownloadExecutionContext context) =>
        DownloadMediaContract.SelectVideo(context, context.PlayUrl);

    private static PlayUrlDashVideo? SelectVideo(
        DownloadExecutionContext context,
        PlayUrl? playUrl)
        => DownloadMediaContract.SelectVideo(context, playUrl);

    private async Task<OperationResult<IReadOnlyList<string>>> RefreshAddressesAsync(
        DownloadExecutionContext context,
        Func<PlayUrl, PlayUrlDashVideo?> selectRefreshedMedia,
        CancellationToken cancellationToken)
    {
        var playback = await _playbackResolver.ResolveAsync(
            context,
            cancellationToken).ConfigureAwait(true);
        if (!playback.TryGetValue(out var playUrl))
        {
            return OperationResult.Failure<IReadOnlyList<string>>(playback.Error!);
        }

        context.PlayUrl = playUrl;
        var media = selectRefreshedMedia(playUrl);
        if (media == null)
        {
            return OperationResult.Failure<IReadOnlyList<string>>(
                DownloadMediaContract.SelectionUnavailable(
                    "The refreshed media stream does not match the finalized selection."));
        }

        var addresses = CreateAddresses(media);
        RequireSecureTransferSchemes(addresses);
        return OperationResult.Success<IReadOnlyList<string>>(addresses);
    }

    private static PlayUrlDashVideo? SelectDurl(PlayUrl playUrl, int order)
    {
        if (!TryValidateAndOrderDurls(playUrl.Durl, out var durls))
        {
            return null;
        }

        var durl = durls.FirstOrDefault(candidate => candidate.Order == order);
        return durl == null ? null : CreateDurlDownloadDescriptor([durl]);
    }

    private static bool TryValidateAndOrderDurls(
        IEnumerable<PlayUrlDurl> source,
        out PlayUrlDurl[] orderedDurls)
    {
        var durls = source.ToArray();
        if (durls.GroupBy(durl => durl.Order).Any(group => group.Count() > 1) ||
            durls.Any(durl =>
                string.IsNullOrWhiteSpace(durl.SourceAddress) &&
                !(durl.BackupUrl?.Any(address => !string.IsNullOrWhiteSpace(address)) ?? false)))
        {
            orderedDurls = [];
            return false;
        }

        orderedDurls = durls.OrderBy(durl => durl.Order).ToArray();
        return true;
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

    private static void RequireSecureTransferSchemes(List<string> urls)
    {
        for (var index = 0; index < urls.Count; index++)
        {
            var url = urls[index];
            if (url.StartsWith("http://", StringComparison.Ordinal))
            {
                urls[index] = "https://" + url["http://".Length..];
            }
        }
    }

    private sealed class PendingDurlDownload(PlayUrlDurl durl)
    {
        public PlayUrlDurl Durl { get; } = durl;

        public DownloadedMediaTransfer? Transfer { get; set; }
    }

    private sealed record DownloadedMediaTransfer(string Key, string FilePath);
}
