using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Media;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;

namespace DownKyi.Services.Download;

internal sealed class MuxStage : IDownloadPipelineStage
{
    private readonly DownloadActivityPresenter _presenter;
    private readonly IFfmpegMediaMuxer _ffmpegProcessor;
    private readonly DownloadTaskStateWriter _stateWriter;

    public MuxStage(
        DownloadActivityPresenter presenter,
        IFfmpegMediaMuxer ffmpegProcessor,
        DownloadTaskStateWriter stateWriter)
    {
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _ffmpegProcessor = ffmpegProcessor ?? throw new ArgumentNullException(nameof(ffmpegProcessor));
        _stateWriter = stateWriter ?? throw new ArgumentNullException(nameof(stateWriter));
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

        if (context.TryReuseStagedMedia())
        {
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
        var finalFile = DownloadOutputLayout.GetDashMediaPath(
            context.WorkingBasePath,
            context.VideoFile != null,
            context.Input.Metadata.AudioCodec.Id,
            context.Input.VideoSettings.IsTranscodingAacToMp3);
        var result = await _ffmpegProcessor.MergeMediaAsync(
            context.Input.VideoSettings,
            context.AudioFile,
            context.VideoFile,
            finalFile,
            overwriteDestination: false,
            embeddedAudioMode: FfmpegEmbeddedAudioMode.Excluded,
            cover: GetCoverForMux(context),
            cancellationToken).ConfigureAwait(true);
        await DownloadOutputRecorder.RecordFileSizeAsync(
            context.TaskId,
            result.Succeeded ? finalFile : null,
            _stateWriter,
            cancellationToken).ConfigureAwait(true);
        context.OutputMedia = result.Succeeded ? finalFile : null;
        context.MediaSucceeded = result.Succeeded;
        return result.Succeeded
            ? DownloadStageResult.Success(Name)
            : CreateMuxFailure(
                result,
                "download.mux.dash",
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
            var finalFile = DownloadOutputLayout.GetDurlMediaPath(context.WorkingBasePath);
            var mergeResult = await _ffmpegProcessor.MergeMediaAsync(
                context.Input.VideoSettings,
                audio: null,
                video: context.DurlDownloads[0].FilePath,
                destination: finalFile,
                overwriteDestination: false,
                embeddedAudioMode: GetEmbeddedAudioMode(context),
                cover: GetCoverForMux(context),
                cancellationToken).ConfigureAwait(true);
            await DownloadOutputRecorder.RecordFileSizeAsync(
                context.TaskId,
                mergeResult.Succeeded ? finalFile : null,
                _stateWriter,
                cancellationToken).ConfigureAwait(true);
            context.OutputMedia = mergeResult.Succeeded ? finalFile : null;
            context.MediaSucceeded = mergeResult.Succeeded;
            return mergeResult.Succeeded
                ? DownloadStageResult.Success(Name)
                : CreateMuxFailure(
                    mergeResult,
                    "download.mux.durl",
                    "The media segment could not be finalized.");
        }

        await _presenter.ShowConcatenatingAsync(context, cancellationToken).ConfigureAwait(true);
        var outputPath = DownloadOutputLayout.GetDurlMediaPath(context.WorkingBasePath);
        var segments = context.DurlDownloads
            .OrderBy(download => download.Durl.Order)
            .Select(download => new FfmpegConcatSegment(
                download.Durl.Order,
                download.FilePath))
            .ToArray();
        var result = await _ffmpegProcessor.ConcatDurlVideosAsync(
            context.Input.VideoSettings,
            segments,
            outputPath,
            overwriteDestination: false,
            embeddedAudioMode: GetEmbeddedAudioMode(context),
            cover: GetCoverForMux(context),
            cancellationToken: cancellationToken).ConfigureAwait(true);
        await DownloadOutputRecorder.RecordFileSizeAsync(
            context.TaskId,
            result.Succeeded ? result.OutputPath : null,
            _stateWriter,
            cancellationToken).ConfigureAwait(true);
        context.OutputMedia = result.Succeeded ? result.OutputPath : null;
        context.MediaSucceeded = result.Succeeded;
        return result.Succeeded
            ? DownloadStageResult.Success(Name)
            : CreateMuxFailure(
                result,
                "download.mux.concat",
                "Segmented media could not be concatenated.");
    }

    private static string? GetCoverForMux(DownloadExecutionContext context) =>
        context.NeedsVideo && context.NeedsCover
            ? context.GetPreferredUsableCoverFile()
            : null;

    private static OperationResult<DownloadStageResult> CreateMuxFailure(
        FfmpegOperationResult result,
        string muxCode,
        string muxMessage) =>
        result.FailureStage == FfmpegOperationFailureStage.Cover
            ? result.FailureKind is FfmpegOperationFailureKind.ProcessFailure
                or FfmpegOperationFailureKind.OutputInvalid
                ? DownloadStageResult.Failure(
                    "download.cover.convert",
                    "The downloaded cover could not be converted for MP4 output.")
                : DownloadStageResult.Failure(
                    "download.cover.process",
                    "The cover conversion process could not be completed.")
            : DownloadStageResult.Failure(muxCode, muxMessage);

    private static FfmpegEmbeddedAudioMode GetEmbeddedAudioMode(
        DownloadExecutionContext context) =>
        context.NeedsAudio
            ? FfmpegEmbeddedAudioMode.Required
            : FfmpegEmbeddedAudioMode.Excluded;

}
