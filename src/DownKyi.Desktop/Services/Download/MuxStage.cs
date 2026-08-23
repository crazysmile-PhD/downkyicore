using System;
using System.Collections.Generic;
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
        var invalidation = result.Succeeded
            ? SourceInvalidationOutcome.None
            : await InvalidateSourcesAsync(
                context,
                result,
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
                GetFailureCode("download.mux.dash", invalidation),
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
            var singleInvalidation = mergeResult.Succeeded
                ? SourceInvalidationOutcome.None
                : await InvalidateSourcesAsync(
                    context,
                    mergeResult,
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
                    GetFailureCode("download.mux.durl", singleInvalidation),
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
        var invalidation = result.Succeeded
            ? SourceInvalidationOutcome.None
            : await InvalidateSourcesAsync(
                context,
                result,
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
                GetFailureCode("download.mux.concat", invalidation),
                "Segmented media could not be concatenated.");
    }

    private async Task<SourceInvalidationOutcome> InvalidateSourcesAsync(
        DownloadExecutionContext context,
        FfmpegOperationResult operationResult,
        CancellationToken cancellationToken)
    {
        if (operationResult.FailureKind != FfmpegOperationFailureKind.InvalidInput ||
            operationResult.InvalidInputPaths.Count == 0)
        {
            return SourceInvalidationOutcome.None;
        }

        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var invalidPaths = operationResult.InvalidInputPaths
            .Select(Path.GetFullPath)
            .ToHashSet(pathComparer);
        var sources = new[]
            {
                (Key: context.AudioTransferKey, FilePath: context.AudioFile),
                (Key: context.VideoTransferKey, FilePath: context.VideoFile)
            }
            .Concat(context.DurlDownloads.Select(download =>
                (Key: (string?)download.TransferKey, FilePath: (string?)download.FilePath)));
        var invalidSources = sources
            .Where(source => !string.IsNullOrWhiteSpace(source.Key) &&
                             !string.IsNullOrWhiteSpace(source.FilePath) &&
                             invalidPaths.Contains(Path.GetFullPath(source.FilePath)))
            .Select(source => new DownloadTransferReference(source.Key!, source.FilePath!))
            .Distinct()
            .ToArray();
        if (invalidSources.Length == 0)
        {
            return SourceInvalidationOutcome.None;
        }

        var cleanedKeys = new List<string>(invalidSources.Length);
        var cleanupFailed = false;
        foreach (var source in invalidSources)
        {
            var cleanup = DownloadTransferFileCleanup.DeleteInvalidArtifacts(
                source.FilePath,
                _logger);
            if (cleanup.Succeeded)
            {
                cleanedKeys.Add(source.Key);
            }
            else
            {
                cleanupFailed = true;
            }
        }

        if (cleanedKeys.Count > 0)
        {
            await _stateWriter.InvalidateCompletedFilesAsync(
                context.TaskId,
                cleanedKeys,
                cancellationToken).ConfigureAwait(true);
        }

        return cleanupFailed
            ? SourceInvalidationOutcome.CleanupFailed
            : SourceInvalidationOutcome.Invalidated;
    }

    private static string GetFailureCode(
        string defaultCode,
        SourceInvalidationOutcome invalidation)
    {
        return invalidation switch
        {
            SourceInvalidationOutcome.Invalidated => "download.mux.invalid-source",
            SourceInvalidationOutcome.CleanupFailed => "download.mux.invalid-source-cleanup",
            _ => defaultCode
        };
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

    private sealed record DownloadTransferReference(string Key, string FilePath);

    private enum SourceInvalidationOutcome
    {
        None,
        Invalidated,
        CleanupFailed
    }
}
