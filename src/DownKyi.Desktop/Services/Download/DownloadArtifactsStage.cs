using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Domain.Results;

namespace DownKyi.Services.Download;

internal sealed class DownloadArtifactsStage : IDownloadPipelineStage
{
    private readonly DownloadArtifactWriter _artifactWriter;
    private readonly DownloadActivityPresenter _presenter;

    public DownloadArtifactsStage(
        DownloadArtifactWriter artifactWriter,
        DownloadActivityPresenter presenter)
    {
        _artifactWriter = artifactWriter ?? throw new ArgumentNullException(nameof(artifactWriter));
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
    }

    public string Name => nameof(DownloadArtifactsStage);

    public async Task<OperationResult<DownloadStageResult>> ExecuteAsync(
        DownloadExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var input = context.Input;
        if (input.NfoRequest != null)
        {
            var nfoFile = DownloadOutputLayout.GetNfoPath(context.WorkingBasePath);
            var nfoResult = await ProduceAndStageAsync(
                context, "nfo", nfoFile,
                () => _artifactWriter.GenerateNfoFileAsync(
                    context.TaskId, nfoFile, input.NfoRequest, cancellationToken),
                cancellationToken).ConfigureAwait(true);
            if (!nfoResult.IsSuccess)
            {
                return StageFailure(nfoResult.Error);
            }
        }

        if (context.NeedsDanmaku)
        {
            await _presenter.ShowDownloadingArtifactAsync(
                context,
                "DownloadingDanmaku",
                cancellationToken).ConfigureAwait(true);
            var danmakuOutputs = DownloadOutputLayout.GetDanmakuOutputPaths(
                context.WorkingBasePath,
                input.DanmakuSettings.OutputFormat);
            var canReuseOutputs = danmakuOutputs.All(output =>
                context.HasPublished(DownloadArtifactWriter.GetDanmakuTransferKey(output.Kind)) ||
                IsDanmakuOutputUsable(output));
            if (!canReuseOutputs && danmakuOutputs.Any(output =>
                    context.HasPublished(DownloadArtifactWriter.GetDanmakuTransferKey(output.Kind))))
            {
                return StageFailure(OperationError.Unexpected(
                    "download.artifact.danmaku.snapshot-incomplete",
                    "The shared danmaku snapshot is incomplete and cannot be recreated safely."));
            }

            var danmakuResult = canReuseOutputs
                ? OperationResult.Success(DownloadArtifactWriteResult.Created(
                    danmakuOutputs.Select(output => output.File).ToArray()))
                : await _artifactWriter.DownloadDanmakuAsync(
                    context.TaskId, input.Metadata, danmakuOutputs,
                    input.DanmakuSettings, cancellationToken).ConfigureAwait(true);
            if (!danmakuResult.IsSuccess)
            {
                return StageFailure(danmakuResult.Error);
            }

            foreach (var output in danmakuOutputs)
            {
                context.RecordUnpublishedArtifact(
                    DownloadArtifactWriter.GetDanmakuTransferKey(output.Kind),
                    output.File);
            }

            context.DanmakuFile = danmakuOutputs
                .Where(output => output.Kind == DownloadDanmakuOutputKind.Ass)
                .Select(output => output.File)
                .SingleOrDefault();
        }

        context.EnsureActive(cancellationToken);
        if (context.NeedsSubtitle)
        {
            await _presenter.ShowDownloadingArtifactAsync(
                context,
                "DownloadingSubtitle",
                cancellationToken).ConfigureAwait(true);
            var subtitleResult = TryGetReusableSubtitles(context, out var reusableSubtitles)
                ? OperationResult.Success(reusableSubtitles)
                : await _artifactWriter.DownloadSubtitleAsync(
                    context.TaskId, input.Metadata, context.WorkingBasePath,
                    input.RequestedContent, cancellationToken).ConfigureAwait(true);
            if (!subtitleResult.TryGetValue(out var subtitles))
            {
                return StageFailure(subtitleResult.Error);
            }

            foreach (var subtitle in subtitles.TrackFiles)
            {
                context.RecordUnpublishedArtifact(
                    DownloadArtifactWriter.GetSubtitleArtifactKey(subtitle.Key),
                    subtitle.Value);
            }

            if (subtitles.DefaultFile != null)
            {
                context.RecordUnpublishedArtifact(
                    DownloadArtifactWriter.DefaultSubtitleArtifactKey,
                    subtitles.DefaultFile);
            }

            context.SubtitleFiles = subtitles.Files;
            context.SubtitleTrackFiles = subtitles.TrackFiles;
            context.DefaultSubtitleFile = subtitles.DefaultFile;
        }

        context.EnsureActive(cancellationToken);
        if (context.NeedsCover)
        {
            var coverResult = await DownloadSelectedCoverAsync(context, cancellationToken)
                .ConfigureAwait(true);
            if (!coverResult.IsSuccess)
            {
                return coverResult;
            }
        }

        context.EnsureActive(cancellationToken);
        return DownloadStageResult.Success(Name);
    }

    private async Task<OperationResult<DownloadStageResult>> DownloadSelectedCoverAsync(
        DownloadExecutionContext context,
        CancellationToken cancellationToken)
    {
        await _presenter.ShowDownloadingArtifactAsync(
            context,
            "DownloadingCover",
            cancellationToken).ConfigureAwait(true);
        var metadata = context.Input.Metadata;
        var pageCover = new CoverCandidate(
            DownloadArtifactWriter.PageCoverTransferKey,
            metadata.PageCoverAddress,
            DownloadOutputLayout.GetPageCoverPath(
                context.WorkingBasePath, metadata.PageCoverAddress));
        var mainCover = new CoverCandidate(
            DownloadArtifactWriter.MainCoverTransferKey,
            metadata.CoverAddress,
            DownloadOutputLayout.GetMainCoverPath(
                context.WorkingBasePath, metadata.CoverAddress));
        if (context.Input.StreamType == PlayStreamType.Bangumi)
        {
            return await DownloadCoverCandidatesAsync(
                context,
                [pageCover, mainCover],
                stopAfterFirstCreated: false,
                cancellationToken).ConfigureAwait(true);
        }

        var candidates = context.Input.StreamType == PlayStreamType.Cheese
            ? new[] { pageCover, mainCover }
            : new[] { mainCover, pageCover };

        // Finish already-created outputs from older tasks without creating another image.
        var existing = candidates.Where(candidate =>
            context.HasPublished(candidate.Key)
            || context.StagingDirectory != null
            && DownloadFileIntegrity.Check(candidate.File).IsUsable).ToArray();
        return await DownloadCoverCandidatesAsync(
            context,
            existing.Length == 0 ? candidates : existing,
            stopAfterFirstCreated: existing.Length == 0,
            cancellationToken).ConfigureAwait(true);
    }

    private async Task<OperationResult<DownloadStageResult>> DownloadCoverCandidatesAsync(
        DownloadExecutionContext context,
        IReadOnlyList<CoverCandidate> candidates,
        bool stopAfterFirstCreated,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in candidates)
        {
            var result = await ProduceAndStageAsync(
                context, candidate.Key, candidate.File,
                () => _artifactWriter.DownloadCoverAsync(
                    context.TaskId, candidate.Address, candidate.File,
                    candidate.Key, cancellationToken),
                cancellationToken).ConfigureAwait(true);
            if (!result.TryGetValue(out var cover))
            {
                return StageFailure(result.Error);
            }

            var file = cover.Files.SingleOrDefault();
            if (file == null
                && context.PublishedArtifacts.TryGetValue(candidate.Key, out var publishedFile)
                && DownloadFileIntegrity.Check(publishedFile).IsUsable)
            {
                file = publishedFile;
            }

            if (candidate.Key == DownloadArtifactWriter.MainCoverTransferKey)
            {
                context.CoverFile = file;
            }
            else
            {
                context.PageCoverFile = file;
            }

            if (context.PreferredCoverFile == null
                && DownloadFileIntegrity.Check(file).IsUsable)
            {
                context.PreferredCoverFile = file;
            }

            if (stopAfterFirstCreated && (file != null || context.HasPublished(candidate.Key)))
            {
                break;
            }
        }

        return DownloadStageResult.Success(Name);
    }

    private sealed record CoverCandidate(string Key, string Address, string File);

    private static async Task<OperationResult<DownloadArtifactWriteResult>> ProduceAndStageAsync(
        DownloadExecutionContext context,
        string key,
        string stagedFile,
        Func<Task<OperationResult<DownloadArtifactWriteResult>>> produce,
        CancellationToken cancellationToken)
    {
        if (context.HasPublished(key))
        {
            return OperationResult.Success(DownloadArtifactWriteResult.NotAvailable());
        }

        var result = context.StagingDirectory != null && DownloadFileIntegrity.Check(stagedFile).IsUsable
            ? OperationResult.Success(DownloadArtifactWriteResult.Created(stagedFile))
            : await produce().ConfigureAwait(true);
        if (!result.TryGetValue(out var artifact))
        {
            return result;
        }

        foreach (var file in artifact.Files)
        {
            context.RecordUnpublishedArtifact(key, file);
        }

        return result;
    }

    private static bool TryGetReusableSubtitles(
        DownloadExecutionContext context,
        out DownloadSubtitleWriteResult result)
    {
        if (context.StagingDirectory == null)
        {
            result = DownloadSubtitleWriteResult.NotAvailable();
            return false;
        }

        var trackFiles = new Dictionary<long, string>();
        if (context.SubtitleTrackFiles != null)
        {
            foreach (var trackFile in context.SubtitleTrackFiles)
            {
                if (DownloadFileIntegrity.Check(trackFile.Value).IsUsable)
                {
                    trackFiles[trackFile.Key] = trackFile.Value;
                }
            }
        }

        foreach (var transferFile in context.Input.TransferFiles)
        {
            if (DownloadArtifactWriter.TryGetSubtitleTrackIdFromTransferKey(
                    transferFile.Key,
                    out var trackId)
                && DownloadFileIntegrity.Check(transferFile.Value).IsUsable)
            {
                trackFiles[trackId] = transferFile.Value;
            }
        }

        foreach (var artifact in context.PublishedArtifacts)
        {
            if (DownloadArtifactWriter.TryGetSubtitleTrackIdFromArtifactKey(
                    artifact.Key,
                    out var trackId)
                && DownloadFileIntegrity.Check(artifact.Value).IsUsable)
            {
                trackFiles[trackId] = artifact.Value;
            }
        }

        var defaultFile = DownloadFileIntegrity.Check(context.DefaultSubtitleFile).IsUsable
            ? context.DefaultSubtitleFile
            : null;
        if (context.PublishedArtifacts.TryGetValue(
                DownloadArtifactWriter.DefaultSubtitleArtifactKey,
                out var publishedDefault)
            && DownloadFileIntegrity.Check(publishedDefault).IsUsable)
        {
            defaultFile = publishedDefault;
        }
        else if (context.Input.TransferFiles.TryGetValue(
                     DownloadArtifactWriter.DefaultSubtitleTransferKey,
                     out var stagedDefault)
                 && DownloadFileIntegrity.Check(stagedDefault).IsUsable)
        {
            defaultFile = stagedDefault;
        }

        var requested = context.Input.RequestedContent;
        var isComplete = requested.SelectedSubtitleTrackIds is { } selectedTrackIds
            ? selectedTrackIds.All(trackFiles.ContainsKey)
              && (requested.DefaultSubtitleTrackId == null || defaultFile != null)
            : trackFiles.Count > 0 && defaultFile != null;
        result = DownloadSubtitleWriteResult.Created(trackFiles, defaultFile);
        return isComplete;
    }

    private static OperationResult<DownloadStageResult> StageFailure(OperationError? error)
    {
        return OperationResult.Failure<DownloadStageResult>(
            error ?? OperationError.Unexpected(
                "download.artifact.unknown",
                "A requested download artifact could not be created."));
    }

    private static bool IsDanmakuOutputUsable(DownloadDanmakuOutputPath output)
    {
        return output.Kind == DownloadDanmakuOutputKind.Xml
            ? DownloadFileIntegrity.CheckXml(output.File, "i").IsUsable
            : DownloadFileIntegrity.Check(output.File).IsUsable;
    }
}
