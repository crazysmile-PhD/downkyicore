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
            var nfoResult = await _artifactWriter.GenerateNfoFileAsync(
                context.TaskId,
                input.OutputBasePath,
                input.NfoRequest,
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
            var danmakuResult = await _artifactWriter.DownloadDanmakuAsync(
                context.TaskId,
                input.Metadata,
                input.OutputBasePath,
                input.DanmakuSettings,
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
            var subtitleResult = await _artifactWriter.DownloadSubtitleAsync(
                context.TaskId,
                input.Metadata,
                input.OutputBasePath,
                cancellationToken).ConfigureAwait(true);
            if (!subtitleResult.TryGetValue(out var subtitles))
            {
                return StageFailure(subtitleResult.Error);
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
                $"{input.OutputBasePath}.{GetImageExtension(input.Metadata.PageCoverAddress)}";
            var pageCoverResult = await _artifactWriter.DownloadCoverAsync(
                context.TaskId,
                input.Metadata.PageCoverAddress,
                pageCoverFileName,
                DownloadArtifactWriter.PageCoverTransferKey,
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
                $"{input.OutputBasePath}.Cover.{GetImageExtension(input.Metadata.CoverAddress)}";
            var coverResult = await _artifactWriter.DownloadCoverAsync(
                context.TaskId,
                input.Metadata.CoverAddress,
                coverFileName,
                DownloadArtifactWriter.MainCoverTransferKey,
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
