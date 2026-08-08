using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Core.FFmpeg;
using DownKyi.Core.Settings;
using DownKyi.Domain.Results;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

internal sealed class MuxStage : IDownloadPipelineStage
{
    private readonly DownloadActivityPresenter _presenter;
    private readonly IFfmpegMediaMuxer _ffmpegProcessor;
    private readonly DownloadTaskStateWriter _stateWriter;
    private readonly ILogger<MuxStage> _logger;

    public MuxStage(
        DownloadActivityPresenter presenter,
        IFfmpegMediaMuxer ffmpegProcessor,
        DownloadTaskStateWriter stateWriter,
        ILogger<MuxStage> logger)
    {
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _ffmpegProcessor = ffmpegProcessor ?? throw new ArgumentNullException(nameof(ffmpegProcessor));
        _stateWriter = stateWriter ?? throw new ArgumentNullException(nameof(stateWriter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string Name => nameof(MuxStage);

    public async Task<OperationResult<DownloadStageResult>> ExecuteAsync(
        DownloadExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.EnsureActive(cancellationToken);
        if (!context.NeedsMedia)
        {
            context.MediaSucceeded = true;
            return DownloadStageResult.Success(Name);
        }

        return context.MediaKind switch
        {
            DownloadMediaKind.Dash => await MuxDashAsync(
                context,
                cancellationToken).ConfigureAwait(true),
            DownloadMediaKind.Durl => await MuxDurlAsync(
                context,
                cancellationToken).ConfigureAwait(true),
            _ => DownloadStageResult.Failure(
                "download.mux.media-kind",
                "The resolved media type cannot be finalized.")
        };
    }

    private async Task<OperationResult<DownloadStageResult>> MuxDashAsync(
        DownloadExecutionContext context,
        CancellationToken cancellationToken)
    {
        await _presenter.ShowMuxingAsync(context, cancellationToken).ConfigureAwait(true);
        var downloading = context.Downloading;
        var finalFile = GetDashOutputPath(context);
        var result = await _ffmpegProcessor.MergeMediaAsync(
            context.Settings.Video,
            context.AudioFile,
            context.VideoFile,
            finalFile,
            overwriteDestination: false,
            cancellationToken).ConfigureAwait(true);
        var invalidSource = !result.Succeeded && await InvalidateSourcesAsync(
            context,
            result.InvalidInputPaths,
            cancellationToken).ConfigureAwait(true);
        downloading.FileSize = await DownloadOutputRecorder.RecordFileSizeAsync(
            context.TaskId,
            result.Succeeded ? finalFile : null,
            _stateWriter,
            cancellationToken).ConfigureAwait(true);
        context.OutputMedia = result.Succeeded ? finalFile : null;
        context.MediaSucceeded = result.Succeeded;
        return result.Succeeded
            ? DownloadStageResult.Success(Name)
            : DownloadStageResult.Failure(
                invalidSource ? "download.mux.invalid-source" : "download.mux.dash",
                "Audio and video streams could not be finalized.");
    }

    private async Task<OperationResult<DownloadStageResult>> MuxDurlAsync(
        DownloadExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (context.DurlDownloads.Count == 0)
        {
            return DownloadStageResult.Failure(
                "download.mux.durl-input",
                "Segmented media files are unavailable.");
        }

        if (context.DurlDownloads.Count == 1)
        {
            await _presenter.ShowMuxingAsync(context, cancellationToken).ConfigureAwait(true);
            var finalFile = $"{context.Downloading.DownloadBase.FilePath}.mp4";
            var mergeResult = await _ffmpegProcessor.MergeMediaAsync(
                context.Settings.Video,
                audio: null,
                video: context.DurlDownloads[0].FilePath,
                destination: finalFile,
                overwriteDestination: false,
                cancellationToken).ConfigureAwait(true);
            var invalidSingleSource = !mergeResult.Succeeded && await InvalidateSourcesAsync(
                context,
                mergeResult.InvalidInputPaths,
                cancellationToken).ConfigureAwait(true);
            context.Downloading.FileSize = await DownloadOutputRecorder.RecordFileSizeAsync(
                context.TaskId,
                mergeResult.Succeeded ? finalFile : null,
                _stateWriter,
                cancellationToken).ConfigureAwait(true);
            context.OutputMedia = mergeResult.Succeeded ? finalFile : null;
            context.MediaSucceeded = mergeResult.Succeeded;
            return mergeResult.Succeeded
                ? DownloadStageResult.Success(Name)
                : DownloadStageResult.Failure(
                    invalidSingleSource ? "download.mux.invalid-source" : "download.mux.durl",
                    "The media segment could not be finalized.");
        }

        await _presenter.ShowConcatenatingAsync(context, cancellationToken).ConfigureAwait(true);
        var outputPath = $"{context.Downloading.DownloadBase.FilePath}.mp4";
        var segments = context.DurlDownloads
            .OrderBy(download => download.Durl.Order)
            .Select(download => new FfmpegConcatSegment(
                download.Durl.Order,
                download.FilePath,
                TimeSpan.FromMilliseconds(download.Durl.Length)))
            .ToArray();
        var result = await _ffmpegProcessor.ConcatDurlVideosAsync(
            context.Settings.Video,
            segments,
            outputPath,
            overwriteDestination: false,
            cancellationToken: cancellationToken).ConfigureAwait(true);
        var invalidSource = !result.Succeeded && await InvalidateSourcesAsync(
            context,
            result.InvalidInputPaths,
            cancellationToken).ConfigureAwait(true);
        context.Downloading.FileSize = await DownloadOutputRecorder.RecordFileSizeAsync(
            context.TaskId,
            result.Succeeded ? result.OutputPath : null,
            _stateWriter,
            cancellationToken).ConfigureAwait(true);
        context.OutputMedia = result.Succeeded ? result.OutputPath : null;
        context.MediaSucceeded = result.Succeeded;
        return result.Succeeded
            ? DownloadStageResult.Success(Name)
            : DownloadStageResult.Failure(
                invalidSource ? "download.mux.invalid-source" : "download.mux.concat",
                "Segmented media could not be concatenated.");
    }

    private async Task<bool> InvalidateSourcesAsync(
        DownloadExecutionContext context,
        IReadOnlyList<string> invalidInputPaths,
        CancellationToken cancellationToken)
    {
        if (invalidInputPaths.Count == 0)
        {
            return false;
        }

        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var invalidPaths = invalidInputPaths
            .Select(Path.GetFullPath)
            .ToHashSet(pathComparer);
        var sources = new[]
            {
                new DownloadTransferReference(context.AudioTransferKey, context.AudioFile),
                new DownloadTransferReference(context.VideoTransferKey, context.VideoFile)
            }
            .Concat(context.DurlDownloads.Select(download =>
                new DownloadTransferReference(download.TransferKey, download.FilePath)));
        var invalidated = false;
        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source.Key) ||
                string.IsNullOrWhiteSpace(source.FilePath) ||
                !invalidPaths.Contains(Path.GetFullPath(source.FilePath)))
            {
                continue;
            }

            DownloadTransferFileCleanup.DeleteInvalidArtifacts(source.FilePath, _logger);
            await _stateWriter.InvalidateCompletedFileAsync(
                context.TaskId,
                source.Key,
                cancellationToken).ConfigureAwait(true);
            invalidated = true;
        }

        return invalidated;
    }

    internal static string GetDashOutputPath(DownloadExecutionContext context)
    {
        if (context.VideoFile != null)
        {
            return $"{context.Downloading.DownloadBase.FilePath}.mp4";
        }

        if (context.Settings.Video.IsTranscodingAacToMp3 == AllowStatus.Yes)
        {
            return $"{context.Downloading.DownloadBase.FilePath}.mp3";
        }

        return context.Downloading.AudioCodec.Id == 30251
            ? $"{context.Downloading.DownloadBase.FilePath}.flac"
            : $"{context.Downloading.DownloadBase.FilePath}.aac";
    }

    private sealed record DownloadTransferReference(string? Key, string? FilePath);
}
