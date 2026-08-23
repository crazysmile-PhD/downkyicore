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

    public DownloadArtifactsStage(DownloadArtifactWriter artifactWriter)
    {
        _artifactWriter = artifactWriter ?? throw new ArgumentNullException(nameof(artifactWriter));
    }

    public string Name => nameof(DownloadArtifactsStage);

    public async Task<OperationResult<DownloadStageResult>> ExecuteAsync(
        DownloadExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var downloading = context.Downloading;
        if (context.Settings.Video.Content.GenerateMovieMetadata)
        {
            var nfoResult = await _artifactWriter.GenerateNfoFileAsync(
                downloading,
                cancellationToken).ConfigureAwait(true);
            if (!nfoResult.IsSuccess)
            {
                return StageFailure(nfoResult.Error);
            }
        }

        if (context.NeedsDanmaku)
        {
            var danmakuResult = await _artifactWriter.DownloadDanmakuAsync(
                downloading,
                context.Settings.Danmaku,
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
            var subtitleResult = await _artifactWriter.DownloadSubtitleAsync(
                downloading,
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
            var pageCoverFileName =
                $"{downloading.DownloadBase.FilePath}.{GetImageExtension(downloading.DownloadBase.PageCoverUrl)}";
            var pageCoverResult = await _artifactWriter.DownloadCoverAsync(
                downloading,
                downloading.DownloadBase.PageCoverUrl,
                pageCoverFileName,
                DownloadArtifactWriter.PageCoverTransferKey,
                cancellationToken).ConfigureAwait(true);
            if (!pageCoverResult.TryGetValue(out var pageCover))
            {
                return StageFailure(pageCoverResult.Error);
            }

            context.PageCoverFile = pageCover.Files.SingleOrDefault();

            var coverFileName =
                $"{downloading.DownloadBase.FilePath}.Cover.{GetImageExtension(downloading.DownloadBase.CoverUrl)}";
            var coverResult = await _artifactWriter.DownloadCoverAsync(
                downloading,
                downloading.DownloadBase.CoverUrl,
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
