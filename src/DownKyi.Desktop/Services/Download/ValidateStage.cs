using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Core.FFmpeg;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;

namespace DownKyi.Services.Download;

internal sealed class ValidateStage : IDownloadPipelineStage
{
    private readonly IFfmpegMediaStreamValidator _mediaStreamValidator;

    public ValidateStage(IFfmpegMediaStreamValidator mediaStreamValidator)
    {
        _mediaStreamValidator = mediaStreamValidator
            ?? throw new ArgumentNullException(nameof(mediaStreamValidator));
    }

    public string Name => nameof(ValidateStage);

    public async Task<OperationResult<DownloadStageResult>> ExecuteAsync(
        DownloadExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.EnsureActive(cancellationToken);
        DownloadActionResultEvaluator.Evaluate(context);
        if (context.NeedsMedia)
        {
            var hasPublishedMedia = context.PublishedArtifacts.TryGetValue(
                "media",
                out var publishedMedia);
            var mediaFile = hasPublishedMedia ? publishedMedia : context.OutputMedia;
            if (!context.MediaSucceeded ||
                (hasPublishedMedia
                    ? !context.HasUsablePublishedMedia()
                    : !File.Exists(mediaFile)))
            {
                return DownloadStageResult.Failure(
                    "download.validate.media",
                    "The finalized media file is missing or invalid.");
            }

            var requireVideoDecode = context.NeedsVideo && !HasDurlConcatVideoEvidence(context);
            if ((context.NeedsAudio || requireVideoDecode) &&
                !await _mediaStreamValidator.ValidateRequiredStreamsAsync(
                    mediaFile!,
                    context.NeedsAudio,
                    requireVideoDecode,
                    cancellationToken).ConfigureAwait(true))
            {
                return DownloadStageResult.Failure(
                    "download.validate.media",
                    "The finalized media file has a missing or undecodable required audio or video stream.");
            }

        }

        if (context.NeedsDanmaku &&
            DownloadArtifactsStage.GetDanmakuOutputs(
                    context.WorkingBasePath,
                    context.Input.DanmakuSettings.OutputFormat)
                .Any(output => !context.HasPublished(output.Key) && !File.Exists(output.File)))
        {
            return DownloadStageResult.Failure(
                "download.validate.danmaku",
                "The requested danmaku file was not created.");
        }

        if (context.NeedsSubtitle && !HasCompleteSubtitleOutput(context))
        {
            return DownloadStageResult.Failure(
                "download.validate.subtitle",
                "One or more requested subtitle files were not created.");
        }

        if (context.PublishedArtifacts.Values.Any(path => !File.Exists(path)))
        {
            return DownloadStageResult.Failure(
                "download.validate.published-missing",
                "A recorded published artifact is missing.");
        }

        if (!context.ActionResults.HasSucceeded)
        {
            return DownloadStageResult.Failure(
                "download.validate.no-resource",
                "None of the requested download actions produced an output.",
                OperationErrorKind.NotFound);
        }

        return DownloadStageResult.Success(Name);
    }

    private static bool HasDurlConcatVideoEvidence(DownloadExecutionContext context) =>
        context.MediaKind == DownloadMediaKind.Durl && context.DurlDownloads.Count > 1;

    private static bool HasCompleteSubtitleOutput(DownloadExecutionContext context)
    {
        if (context.SubtitleTrackFiles == null)
        {
            return context.SubtitleFiles == null
                   || context.SubtitleFiles.All(subtitle =>
                       context.HasPublished("subtitle:" + Path.GetFileName(subtitle))
                       || File.Exists(subtitle));
        }

        if (context.DefaultSubtitleFile != null
            && !context.HasPublished(DownloadArtifactWriter.DefaultSubtitleArtifactKey)
            && !DownloadFileIntegrity.Check(context.DefaultSubtitleFile).IsUsable)
        {
            return false;
        }

        if (context.Input.RequestedContent.DefaultSubtitleTrackId != null
            && context.DefaultSubtitleFile == null
            && !context.HasPublished(DownloadArtifactWriter.DefaultSubtitleArtifactKey))
        {
            return false;
        }

        IEnumerable<long> trackIds =
            context.Input.RequestedContent.SelectedSubtitleTrackIds is { } selectedTrackIds
                ? selectedTrackIds
                : context.SubtitleTrackFiles.Keys;
        return trackIds.All(trackId =>
            context.SubtitleTrackFiles.TryGetValue(trackId, out var stagedFile)
            && HasUsableSubtitleTrack(context, trackId, stagedFile));
    }

    private static bool HasUsableSubtitleTrack(
        DownloadExecutionContext context,
        long trackId,
        string stagedFile)
    {
        var artifactKey = DownloadArtifactWriter.GetSubtitleArtifactKey(trackId);
        return context.PublishedArtifacts.TryGetValue(artifactKey, out var publishedFile)
               && DownloadFileIntegrity.Check(publishedFile).IsUsable
               || DownloadFileIntegrity.Check(stagedFile).IsUsable;
    }
}
