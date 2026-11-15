using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;

namespace DownKyi.Services.Download;

internal sealed class ValidateStage : IDownloadPipelineStage
{
    public string Name => nameof(ValidateStage);

    public Task<OperationResult<DownloadStageResult>> ExecuteAsync(
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
                return Task.FromResult(DownloadStageResult.Failure(
                    hasPublishedMedia
                        ? "download.validate.published-missing"
                        : "download.validate.media",
                    "The finalized media file is missing or invalid.",
                    OperationErrorKind.Validation));
            }
        }

        if (context.NeedsDanmaku &&
            DownloadOutputLayout.GetDanmakuOutputPaths(
                    context.WorkingBasePath,
                    context.Input.DanmakuSettings.OutputFormat)
                .Any(output => !HasUsableDanmakuOutput(context, output)))
        {
            return Task.FromResult(DownloadStageResult.Failure(
                "download.validate.danmaku",
                "The requested danmaku file was not created or is invalid.",
                OperationErrorKind.Validation));
        }

        if (context.NeedsSubtitle && !HasCompleteSubtitleOutput(context))
        {
            return Task.FromResult(DownloadStageResult.Failure(
                "download.validate.subtitle",
                "One or more requested subtitle files were not created.",
                OperationErrorKind.Validation));
        }

        if (context.PublishedArtifacts.Values.Any(path =>
                !DownloadFileIntegrity.Check(path).IsUsable))
        {
            return Task.FromResult(DownloadStageResult.Failure(
                "download.validate.published-missing",
                "A recorded published artifact is missing or invalid.",
                OperationErrorKind.Validation));
        }

        if (context.UnpublishedArtifacts.Values.Any(path =>
                !DownloadFileIntegrity.Check(path).IsUsable))
        {
            return Task.FromResult(DownloadStageResult.Failure(
                "download.validate.unpublished-invalid",
                "An unpublished download artifact is missing or invalid.",
                OperationErrorKind.Validation));
        }

        if (!context.ActionResults.HasSucceeded)
        {
            return Task.FromResult(DownloadStageResult.Failure(
                "download.validate.no-resource",
                "None of the requested download actions produced an output.",
                OperationErrorKind.NotFound));
        }

        context.MarkArtifactsValidated();
        return Task.FromResult<OperationResult<DownloadStageResult>>(
            DownloadStageResult.Success(Name));
    }

    private static bool HasUsableDanmakuOutput(
        DownloadExecutionContext context,
        DownloadDanmakuOutputPath output)
    {
        var key = DownloadArtifactIdentity.GetDanmakuTransferKey(output.Kind);
        var path = context.PublishedArtifacts.TryGetValue(key, out var published)
            ? published
            : output.File;
        return output.Kind == DownloadDanmakuOutputKind.Xml
            ? DownloadFileIntegrity.CheckXml(path, "i").IsUsable
            : DownloadFileIntegrity.Check(path).IsUsable;
    }

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
            && !context.HasPublished(DownloadArtifactIdentity.DefaultSubtitleArtifactKey)
            && !DownloadFileIntegrity.Check(context.DefaultSubtitleFile).IsUsable)
        {
            return false;
        }

        if (context.Input.RequestedContent.DefaultSubtitleTrackId != null
            && context.DefaultSubtitleFile == null
            && !context.HasPublished(DownloadArtifactIdentity.DefaultSubtitleArtifactKey))
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
        var artifactKey = DownloadArtifactIdentity.GetSubtitleArtifactKey(trackId);
        return context.PublishedArtifacts.TryGetValue(artifactKey, out var publishedFile)
               && DownloadFileIntegrity.Check(publishedFile).IsUsable
               || DownloadFileIntegrity.Check(stagedFile).IsUsable;
    }
}
