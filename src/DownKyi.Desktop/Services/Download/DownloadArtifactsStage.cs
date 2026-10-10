using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Core.Settings;
using DownKyi.Domain.Results;

namespace DownKyi.Services.Download;

internal sealed class DownloadArtifactsStage : IDownloadPipelineStage
{
    private readonly DownloadArtifactWriter _artifactWriter;
    private readonly DownloadActivityPresenter _presenter;
    private readonly DownloadTaskFileService? _fileService;

    public DownloadArtifactsStage(
        DownloadArtifactWriter artifactWriter,
        DownloadActivityPresenter presenter,
        DownloadTaskFileService? fileService = null)
    {
        _artifactWriter = artifactWriter ?? throw new ArgumentNullException(nameof(artifactWriter));
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _fileService = fileService;
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
            var nfoFile = $"{context.WorkingBasePath}.nfo";
            var nfoResult = await ProduceAndPublishAsync(
                context, "nfo", nfoFile,
                () => _artifactWriter.GenerateNfoFileAsync(
                    context.TaskId, context.WorkingBasePath, input.NfoRequest, cancellationToken),
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
            var danmakuOutputs = GetDanmakuOutputs(
                context.WorkingBasePath,
                input.DanmakuSettings.OutputFormat);
            var canReuseOutputs = danmakuOutputs.All(output =>
                context.HasPublished(output.Key) ||
                IsDanmakuOutputUsable(output));
            if (!canReuseOutputs && danmakuOutputs.Any(output => context.HasPublished(output.Key)))
            {
                return StageFailure(OperationError.Unexpected(
                    "download.artifact.danmaku.snapshot-incomplete",
                    "The shared danmaku snapshot is incomplete and cannot be recreated safely."));
            }

            var danmakuResult = canReuseOutputs
                ? OperationResult.Success(DownloadArtifactWriteResult.Created(
                    danmakuOutputs.Select(output => output.File).ToArray()))
                : await _artifactWriter.DownloadDanmakuAsync(
                    context.TaskId, input.Metadata, context.WorkingBasePath,
                    input.DanmakuSettings, cancellationToken).ConfigureAwait(true);
            if (!danmakuResult.IsSuccess)
            {
                return StageFailure(danmakuResult.Error);
            }

            foreach (var output in danmakuOutputs)
            {
                var published = await PublishAsync(
                    context, output.Key, output.File, cancellationToken).ConfigureAwait(true);
                if (!published.IsSuccess)
                {
                    return StageFailure(published.Error);
                }
            }

            context.DanmakuFile = input.DanmakuSettings.OutputFormat.IncludesAss()
                ? $"{context.WorkingBasePath}.ass"
                : null;
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
                var published = await PublishAsync(
                    context,
                    DownloadArtifactWriter.GetSubtitleArtifactKey(subtitle.Key),
                    subtitle.Value,
                    cancellationToken).ConfigureAwait(true);
                if (!published.IsSuccess)
                {
                    return StageFailure(published.Error);
                }
            }

            if (subtitles.DefaultFile != null)
            {
                var published = await PublishAsync(
                    context,
                    DownloadArtifactWriter.DefaultSubtitleArtifactKey,
                    subtitles.DefaultFile,
                    cancellationToken).ConfigureAwait(true);
                if (!published.IsSuccess)
                {
                    return StageFailure(published.Error);
                }
            }

            context.SubtitleFiles = subtitles.Files;
            context.SubtitleTrackFiles = subtitles.TrackFiles;
            context.DefaultSubtitleFile = subtitles.DefaultFile;
        }

        context.EnsureActive(cancellationToken);
        if (context.NeedsCover)
        {
            await _presenter.ShowDownloadingArtifactAsync(
                context,
                "DownloadingCover",
                cancellationToken).ConfigureAwait(true);
            var pageCoverFileName =
                $"{context.WorkingBasePath}.{GetImageExtension(input.Metadata.PageCoverAddress)}";
            var pageCoverResult = await ProduceAndPublishAsync(
                context, "page-cover", pageCoverFileName,
                () => _artifactWriter.DownloadCoverAsync(
                    context.TaskId, input.Metadata.PageCoverAddress, pageCoverFileName,
                    DownloadArtifactWriter.PageCoverTransferKey, cancellationToken),
                cancellationToken).ConfigureAwait(true);
            if (!pageCoverResult.TryGetValue(out var pageCover))
            {
                return StageFailure(pageCoverResult.Error);
            }

            context.PageCoverFile = pageCover.Files.SingleOrDefault();

            await _presenter.ShowDownloadingArtifactAsync(
                context,
                "DownloadingCover",
                cancellationToken).ConfigureAwait(true);
            var coverFileName =
                $"{context.WorkingBasePath}.Cover.{GetImageExtension(input.Metadata.CoverAddress)}";
            var coverResult = await ProduceAndPublishAsync(
                context, "cover", coverFileName,
                () => _artifactWriter.DownloadCoverAsync(
                    context.TaskId, input.Metadata.CoverAddress, coverFileName,
                    DownloadArtifactWriter.MainCoverTransferKey, cancellationToken),
                cancellationToken).ConfigureAwait(true);
            if (!coverResult.TryGetValue(out var cover))
            {
                return StageFailure(coverResult.Error);
            }

            context.CoverFile = cover.Files.SingleOrDefault();
        }

        context.EnsureActive(cancellationToken);
        return DownloadStageResult.Success(Name);
    }

    private async Task<OperationResult<DownloadArtifactWriteResult>> ProduceAndPublishAsync(
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
            var published = await PublishAsync(context, key, file, cancellationToken).ConfigureAwait(true);
            if (!published.IsSuccess)
            {
                return OperationResult.Failure<DownloadArtifactWriteResult>(published.Error!);
            }
        }

        return result;
    }

    private Task<OperationResult> PublishAsync(
        DownloadExecutionContext context,
        string key,
        string file,
        CancellationToken cancellationToken) =>
        context.HasPublished(key) || _fileService == null
            ? Task.FromResult(OperationResult.Success())
            : _fileService.PublishAsync(context, key, file, cancellationToken);

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

    internal static IReadOnlyList<DanmakuOutput> GetDanmakuOutputs(
        string outputBasePath,
        DanmakuOutputFormat outputFormat)
    {
        var outputs = new List<DanmakuOutput>(2);
        if (outputFormat.IncludesAss())
        {
            outputs.Add(new DanmakuOutput(
                DownloadArtifactWriter.DanmakuAssTransferKey,
                $"{outputBasePath}.ass"));
        }

        if (outputFormat.IncludesXml())
        {
            outputs.Add(new DanmakuOutput(
                DownloadArtifactWriter.DanmakuXmlTransferKey,
                $"{outputBasePath}.xml"));
        }

        return outputs;
    }

    private static bool IsDanmakuOutputUsable(DanmakuOutput output)
    {
        return output.Key == DownloadArtifactWriter.DanmakuXmlTransferKey
            ? DownloadFileIntegrity.CheckXml(output.File, "i").IsUsable
            : DownloadFileIntegrity.Check(output.File).IsUsable;
    }

    internal static string GetImageExtension(string? coverUrl)
    {
        if (string.IsNullOrWhiteSpace(coverUrl))
        {
            return string.Empty;
        }

        var candidate = coverUrl.StartsWith("//", StringComparison.Ordinal)
            ? $"{Uri.UriSchemeHttps}:{coverUrl}"
            : coverUrl;
        var path = Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            ? uri.AbsolutePath
            : coverUrl.Split('?', '#')[0];
        return Path.GetExtension(path).TrimStart('.');
    }
}

internal readonly record struct DanmakuOutput(string Key, string File);
