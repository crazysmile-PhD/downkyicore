using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Core.BiliApi.Favorites;
using DownKyi.Core.BiliApi.Favorites.Models;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.Users;
using DownKyi.Core.BiliApi.Users.Models;
using DownKyi.Core.Logging;
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

    public UserSpaceLoadCoordinator(
        IWbiKeyProvider wbiKeyProvider,
        ILogger<UserSpaceLoadCoordinator> logger)
    {
        _wbiKeyProvider = wbiKeyProvider ?? throw new ArgumentNullException(nameof(wbiKeyProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<UserSpaceSnapshot> LoadAsync(long mid, CancellationToken cancellationToken)
    {
        var user = await WbiRequestExecutor.ExecuteAsync(
            _wbiKeyProvider,
            (keys, unixTimeSeconds) => UserInfo.GetUserInfoForSpace(
                keys,
                unixTimeSeconds,
                mid,
                cancellationToken),
            TimeProvider.System,
            cancellationToken).ConfigureAwait(false);
        var publicationTypes = await WbiRequestExecutor.ExecuteAsync(
            _wbiKeyProvider,
            (keys, unixTimeSeconds) => Core.BiliApi.Users.UserSpace.GetPublicationType(
                keys,
                unixTimeSeconds,
                mid),
            TimeProvider.System,
            cancellationToken).ConfigureAwait(false);

        return await Task.Run(() => LoadRemainingSnapshot(
            mid,
            user,
            publicationTypes,
            cancellationToken), cancellationToken).ConfigureAwait(false);
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
                        ? "avares://DownKyi/Resources/video-placeholder.png"
                        : item.Cover,
                    item.Title,
                    item.MediaCount,
                    item.Mtime))
                .ToArray();
    }

    private UserSpaceSnapshot LoadRemainingSnapshot(
        long mid,
        UserInfoForSpace? user,
        IReadOnlyList<SpacePublicationListTypeVideoZone>? publicationTypes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = Core.BiliApi.Users.UserSpace.GetSpaceSettings(mid, cancellationToken);
        var seasonsSeries = Core.BiliApi.Users.UserSpace.GetSeasonsSeries(mid, 1, 20);
        var favoriteFolders = LoadFavoriteFolders(mid, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var relation = UserStatus.GetUserRelationStat(mid, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var statistics = UserStatus.GetUpStat(mid);
        return new UserSpaceSnapshot(
            settings,
            user,
            publicationTypes,
            seasonsSeries,
            favoriteFolders,
            relation,
            statistics);
    }

    private IReadOnlyList<UserSpaceFavoriteFolder> LoadFavoriteFolders(
        long mid,
        CancellationToken cancellationToken)
    {
        try
        {
            return MapFavoriteFolders(FavoritesInfo.GetAllCreatedFavorites(mid, cancellationToken));
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
