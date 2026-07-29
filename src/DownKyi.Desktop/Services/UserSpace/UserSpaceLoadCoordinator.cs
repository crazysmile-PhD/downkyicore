using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Bilibili;
using DownKyi.Application.Diagnostics;
using DownKyi.Core.BiliApi.Favorites;
using DownKyi.Core.BiliApi.Favorites.Models;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.Users;
using DownKyi.Core.BiliApi.Users.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace DownKyi.Services.UserSpace;

internal sealed record UserSpaceFavoriteFolder(
    long Id,
    string Cover,
    string Title,
    int MediaCount,
    long UpdatedAtUnixSeconds);

internal sealed record UserSpaceSnapshot(
    SpaceSettings? Settings,
    UserInfoForSpace? User,
    IReadOnlyList<SpacePublicationListTypeVideoZone>? PublicationTypes,
    SpaceSeasonsSeries? SeasonsSeries,
    IReadOnlyList<UserSpaceFavoriteFolder> FavoriteFolders,
    UserRelationStat? Relation,
    UpStat? Statistics);

internal interface IUserSpaceLoadCoordinator
{
    Task<UserSpaceSnapshot> LoadAsync(long mid, CancellationToken cancellationToken);
}

internal sealed class UserSpaceLoadCoordinator : IUserSpaceLoadCoordinator
{
    private readonly ILogger<UserSpaceLoadCoordinator> _logger;
    private readonly IWbiKeyProvider _wbiKeyProvider;
    private readonly IBilibiliApiClient _client;

    public UserSpaceLoadCoordinator(
        IWbiKeyProvider wbiKeyProvider,
        ILogger<UserSpaceLoadCoordinator> logger,
        IBilibiliApiClient client)
    {
        _wbiKeyProvider = wbiKeyProvider ?? throw new ArgumentNullException(nameof(wbiKeyProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<UserSpaceSnapshot> LoadAsync(long mid, CancellationToken cancellationToken)
    {
        var user = await WbiRequestExecutor.ExecuteAsync(
            _wbiKeyProvider,
            (keys, unixTimeSeconds) => _client.GetUserInfoForSpaceAsync(
                keys,
                unixTimeSeconds,
                mid,
                cancellationToken),
            TimeProvider.System,
            cancellationToken).ConfigureAwait(false);
        var publicationTypes = await WbiRequestExecutor.ExecuteAsync(
            _wbiKeyProvider,
            (keys, unixTimeSeconds) => _client.GetPublicationTypeAsync(
                keys,
                unixTimeSeconds,
                mid,
                cancellationToken),
            TimeProvider.System,
            cancellationToken).ConfigureAwait(false);

        return await LoadRemainingSnapshotAsync(
            mid,
            user,
            publicationTypes,
            cancellationToken).ConfigureAwait(false);
    }

    internal static IReadOnlyList<UserSpaceFavoriteFolder> MapFavoriteFolders(
        IReadOnlyList<FavoritesMetaInfo>? favorites)
    {
        return favorites == null
            ? []
            : favorites
                .Where(item => item.MediaCount > 0)
                .Select(item => new UserSpaceFavoriteFolder(
                    item.Id,
                    string.IsNullOrWhiteSpace(item.Cover)
                        ? "avares://DownKyi.Desktop/Resources/video-placeholder.png"
                        : item.Cover,
                    item.Title,
                    item.MediaCount,
                    item.Mtime))
                .ToArray();
    }

    private async Task<UserSpaceSnapshot> LoadRemainingSnapshotAsync(
        long mid,
        UserInfoForSpace? user,
        IReadOnlyList<SpacePublicationListTypeVideoZone>? publicationTypes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = await _client.GetSpaceSettingsAsync(mid, cancellationToken)
            .ConfigureAwait(false);
        var seasonsSeries = await _client.GetSeasonsSeriesAsync(mid, 1, 20, cancellationToken)
            .ConfigureAwait(false);
        var favoriteFolders = await LoadFavoriteFoldersAsync(mid, cancellationToken)
            .ConfigureAwait(false);
        var relation = await _client.GetUserRelationStatAsync(mid, cancellationToken)
            .ConfigureAwait(false);
        var statistics = await _client.GetUpStatAsync(mid, cancellationToken)
            .ConfigureAwait(false);
        return new UserSpaceSnapshot(
            settings,
            user,
            publicationTypes,
            seasonsSeries,
            favoriteFolders,
            relation,
            statistics);
    }

    private async Task<IReadOnlyList<UserSpaceFavoriteFolder>> LoadFavoriteFoldersAsync(
        long mid,
        CancellationToken cancellationToken)
    {
        try
        {
            var favorites = await _client.GetAllCreatedFavoritesAsync(mid, cancellationToken)
                .ConfigureAwait(false);
            return MapFavoriteFolders(favorites);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException
            or JsonException or FormatException)
        {
            _logger.LogWarningMessage("Public favorite folders could not be loaded for user space.", exception);
            return [];
        }
    }
}
