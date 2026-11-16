using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Diagnostics;
using DownKyi.Core.BiliApi;
using DownKyi.Models;
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

    public async Task<MovieMetadata> BuildAsync(
        VideoInfoView video,
        VideoPage page,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(page);

        var metadata = new MovieMetadata
        {
            Title = page.Name,
            Plot = video.Description,
            Year = page.OriginalPublishTime.Year.ToString(CultureInfo.InvariantCulture),
            Premiered = page.OriginalPublishTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            BilibiliId = new UniqueId("bilibili", page.Bvid)
        };
        metadata.Actors.Add(new Actor(
            page.Owner?.Name ?? string.Empty,
            (page.Owner?.Mid ?? -1).ToString(CultureInfo.InvariantCulture)));
        foreach (var genre in video.VideoZone.Split('>'))
        {
            metadata.Genres.Add(genre);
        }

        foreach (var tag in await LoadOptionalTagsAsync(page, cancellationToken).ConfigureAwait(true))
        {
            metadata.Tags.Add(tag);
        }

        if (video.Score != null)
        {
            metadata.Ratings.Add(new Rating("bilibili", video.Score.Value, isDefault: true));
        }

        return metadata;
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
