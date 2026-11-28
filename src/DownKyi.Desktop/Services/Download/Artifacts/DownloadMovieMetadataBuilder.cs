using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Diagnostics;
using DownKyi.Core.BiliApi;
using DownKyi.Domain.Downloads;
using DownKyi.Presentation;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

internal sealed class DownloadMovieMetadataBuilder
{
    private readonly ILogger<DownloadMovieMetadataBuilder> _logger;

    public DownloadMovieMetadataBuilder(ILogger<DownloadMovieMetadataBuilder> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<DownloadNfoRequest> BuildAsync(
        VideoInfoView video,
        VideoPage page,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(page);

        var tags = await LoadOptionalTagsAsync(page, cancellationToken).ConfigureAwait(true);
        return new DownloadNfoRequest(
            page.Name,
            video.Description,
            page.OriginalPublishTime.Year.ToString(CultureInfo.InvariantCulture),
            video.VideoZone.Split('>').ToImmutableArray(),
            tags.ToImmutableArray(),
            [new DownloadNfoActor(page.Owner?.Name ?? string.Empty,
                (page.Owner?.Mid ?? -1).ToString(CultureInfo.InvariantCulture))],
            new DownloadNfoUniqueId("bilibili", page.Bvid),
            page.OriginalPublishTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            video.Score is { } score
                ? [new DownloadNfoRating("bilibili", score, 10, true)]
                : []);
    }

    private async Task<IReadOnlyList<string>> LoadOptionalTagsAsync(
        VideoPage page,
        CancellationToken cancellationToken)
    {
        try
        {
            return await page.LoadTagsAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException
            or IOException
            or BilibiliApiResponseException
            or Newtonsoft.Json.JsonException)
        {
            _logger.LogWarningMessage(
                "Optional video tags could not be loaded; the download task will continue without tags.",
                exception);
            return Array.Empty<string>();
        }
    }
}
