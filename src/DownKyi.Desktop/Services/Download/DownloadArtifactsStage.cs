using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
            var danmakuFile = $"{context.WorkingBasePath}.ass";
            var danmakuResult = await ProduceAndPublishAsync(
                context, "danmaku", danmakuFile,
                () => _artifactWriter.DownloadDanmakuAsync(
                    context.TaskId, input.Metadata, context.WorkingBasePath,
                    input.DanmakuSettings, cancellationToken),
                cancellationToken).ConfigureAwait(true);
            if (!danmakuResult.TryGetValue(out var danmaku))
            {
                return StageFailure(danmakuResult.Error);
            }

            context.DanmakuFile = danmaku.Files.SingleOrDefault();
        }

        context.EnsureActive(cancellationToken);
        if (context.NeedsSubtitle)
        {
            await _presenter.ShowDownloadingArtifactAsync(
                context,
                "DownloadingSubtitle",
                cancellationToken).ConfigureAwait(true);
            var defaultSubtitle = $"{context.WorkingBasePath}.srt";
            var subtitleResult = context.StagingDirectory != null &&
                                 DownloadFileIntegrity.Check(defaultSubtitle).IsUsable
                ? OperationResult.Success(DownloadArtifactWriteResult.Created(
                    Directory.EnumerateFiles(Path.GetDirectoryName(context.WorkingBasePath)!,
                        Path.GetFileName(context.WorkingBasePath) + "*.srt").ToArray()))
                : await _artifactWriter.DownloadSubtitleAsync(
                    context.TaskId, input.Metadata, context.WorkingBasePath,
                    cancellationToken).ConfigureAwait(true);
            if (!subtitleResult.TryGetValue(out var subtitles))
            {
                return StageFailure(subtitleResult.Error);
            }

            foreach (var subtitle in subtitles.Files)
            {
                var published = await PublishAsync(
                    context, "subtitle:" + Path.GetFileName(subtitle), subtitle,
                    cancellationToken).ConfigureAwait(true);
                if (!published.IsSuccess)
                {
                    return StageFailure(published.Error);
                }
            }

            context.SubtitleFiles = subtitles.Files;
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

    private static OperationResult<DownloadStageResult> StageFailure(OperationError? error)
    {
        return OperationResult.Failure<DownloadStageResult>(
            error ?? OperationError.Unexpected(
                "download.artifact.unknown",
                "A requested download artifact could not be created."));
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
