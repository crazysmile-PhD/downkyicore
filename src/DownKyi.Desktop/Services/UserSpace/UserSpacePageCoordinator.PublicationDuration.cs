using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Diagnostics;
using DownKyi.Core.BiliApi;
using DownKyi.Core.BiliApi.Users;
using DownKyi.Core.BiliApi.Users.Models;
using Newtonsoft.Json;

namespace DownKyi.Services.UserSpace;

internal sealed partial class UserSpacePageCoordinator
{
    private async Task<IReadOnlyDictionary<(long Mid, long SeasonId), (int VideoCount, long Duration)>> LoadPublicationSeasonDurationsAsync(
        IReadOnlyList<SpacePublicationListVideo> videos, CancellationToken cancellationToken)
    {
        var durations = new ConcurrentDictionary<(long Mid, long SeasonId), (int VideoCount, long Duration)>();
        var seasons = videos.Where(IsPublicationSeason).Select(video => (video.Mid, video.SeasonId)).Distinct();
        await Parallel.ForEachAsync(seasons, new ParallelOptions
        {
            MaxDegreeOfParallelism = 3,
            CancellationToken = cancellationToken
        }, async (season, token) =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                durations[season] = await LoadSeasonDurationAsync(season.Mid, season.SeasonId, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                _logger.LogWarningMessage("Publication collection duration lookup timed out.");
            }
            catch (Exception exception) when (exception is BilibiliApiResponseException or HttpRequestException
                or IOException or TimeoutException or JsonException or OverflowException)
            {
                _logger.LogWarningMessage($"Publication collection duration unavailable; error={exception.GetType().Name}.");
            }
        }).ConfigureAwait(false);
        return durations;
    }

    private async Task<(int VideoCount, long Duration)> LoadSeasonDurationAsync(long mid, long seasonId, CancellationToken cancellationToken)
    {
        const int pageSize = 30;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int? expectedTotal = null;
        long duration = 0;
        for (var pageNumber = 1; ; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await _client.GetSeasonsDetailAsync(
                mid, seasonId, pageNumber, pageSize, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (page?.Page == null || page.Archives == null || page.Meta?.SeasonId != seasonId
                || page.Page.PageNum != pageNumber || page.Page.PageSize <= 0 || page.Page.Total < 0
                || expectedTotal.HasValue && page.Page.Total != expectedTotal.Value)
            {
                throw IncompleteSeasonDuration();
            }

            expectedTotal ??= page.Page.Total;
            var previousCount = seen.Count;
            foreach (var archive in page.Archives)
            {
                var identity = archive.Aid > 0 ? $"av{archive.Aid}" : archive.Bvid;
                if (string.IsNullOrWhiteSpace(identity) || archive.Duration <= 0)
                {
                    throw IncompleteSeasonDuration();
                }
                if (seen.Add(identity))
                {
                    duration = checked(duration + archive.Duration);
                }
            }

            if (seen.Count == expectedTotal.Value)
            {
                return (seen.Count, duration);
            }
            // Never present a preview, repeated page or partial sum as the collection total.
            if (seen.Count > expectedTotal.Value || seen.Count == previousCount
                || (long)pageNumber * page.Page.PageSize >= expectedTotal.Value)
            {
                throw IncompleteSeasonDuration();
            }
        }
    }

    private static bool IsPublicationSeason(SpacePublicationListVideo video) =>
        video.IsAvoided != 0 && video.SeasonId > 0 && video.Mid > 0;

    private static BilibiliApiResponseException IncompleteSeasonDuration() =>
        new(nameof(LoadSeasonDurationAsync), "Collection duration requires complete, consistent archive pages.");
}
