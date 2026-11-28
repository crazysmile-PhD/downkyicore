using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Bilibili;
using DownKyi.Core.BiliApi;
using DownKyi.Core.BiliApi.Favorites;
using DownKyi.Core.BiliApi.Favorites.Models;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.Users;
using DownKyi.Core.BiliApi.Users.Models;
using DownKyi.Infrastructure.Bilibili;

namespace DownKyi.Services.UserSpace;

internal sealed record UserSpaceFavoriteFolder(long Id, string Cover, string Title, int MediaCount, long UpdatedAtUnixSeconds);
internal sealed record UserSpaceProfileSnapshot(UserInfoForSpace? User, string Background);
internal sealed record UserSpaceOverviewSnapshot(
    IReadOnlyList<SpacePublicationListTypeVideoZone>? PublicationTypes, UserRelationStat? Relation, UpStat? Statistics);
internal sealed record UserSpaceFavoriteFolderPage(IReadOnlyList<UserSpaceFavoriteFolder> Folders, int PageCount);
internal sealed record UserSpaceSeasonsPage(SpaceSeasonsSeries Items, int PageCount);

internal interface IUserSpaceLoadCoordinator
{
    Task<UserSpaceProfileSnapshot> LoadProfileAsync(long mid, CancellationToken cancellationToken);
    Task<UserSpaceOverviewSnapshot> LoadOverviewAsync(long mid, CancellationToken cancellationToken);
    Task<UserSpaceFavoriteFolderPage> LoadFavoriteFoldersPageAsync(long mid, int page, int pageSize, CancellationToken cancellationToken);
    Task<UserSpaceSeasonsPage> LoadSeasonsSeriesPageAsync(long mid, int page, int pageSize, CancellationToken cancellationToken);
}

internal sealed class UserSpaceLoadCoordinator : IUserSpaceLoadCoordinator
{
    private readonly IWbiKeyProvider _wbiKeyProvider;
    private readonly IBilibiliApiClient _client;

    public UserSpaceLoadCoordinator(IWbiKeyProvider wbiKeyProvider, IBilibiliApiClient client)
    {
        _wbiKeyProvider = wbiKeyProvider ?? throw new ArgumentNullException(nameof(wbiKeyProvider));
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<UserSpaceProfileSnapshot> LoadProfileAsync(long mid, CancellationToken cancellationToken)
    {
        var user = await WbiRequestExecutor.ExecuteAsync(_wbiKeyProvider,
            (keys, unixTimeSeconds) => _client.GetUserInfoForSpaceAsync(keys, unixTimeSeconds,
                mid, LoginHelper.HasSavedLoginInfo, cancellationToken), TimeProvider.System, cancellationToken)
            .ConfigureAwait(false);
        return new UserSpaceProfileSnapshot(user, UserSpaceBackground.Resolve(user?.TopPhoto));
    }

    public async Task<UserSpaceOverviewSnapshot> LoadOverviewAsync(long mid, CancellationToken cancellationToken)
    {
        var types = await WbiRequestExecutor.ExecuteAsync(_wbiKeyProvider,
            (keys, unixTimeSeconds) => _client.GetPublicationTypeAsync(keys, unixTimeSeconds,
                mid, LoginHelper.HasSavedLoginInfo, cancellationToken), TimeProvider.System, cancellationToken)
            .ConfigureAwait(false);
        var relation = await _client.GetUserRelationStatAsync(mid, cancellationToken).ConfigureAwait(false);
        var statistics = await _client.GetUpStatAsync(mid, cancellationToken).ConfigureAwait(false);
        return new UserSpaceOverviewSnapshot(types, relation, statistics);
    }

    public async Task<UserSpaceFavoriteFolderPage> LoadFavoriteFoldersPageAsync(
        long mid, int page, int pageSize, CancellationToken cancellationToken)
    {
        var data = await _client.GetCreatedFavoritesPageAsync(mid, page, pageSize, cancellationToken).ConfigureAwait(false);
        var hasMore = data.List.Count > 0 && (data.Count <= 0 || (long)page * pageSize < data.Count);
        var pageCount = data.Count > 0 ? (int)Math.Ceiling((double)data.Count / pageSize) : page + (hasMore ? 1 : 0);
        return new UserSpaceFavoriteFolderPage(MapFavoriteFolders(data.List), Math.Max(page, pageCount));
    }

    public async Task<UserSpaceSeasonsPage> LoadSeasonsSeriesPageAsync(
        long mid, int page, int pageSize, CancellationToken cancellationToken)
    {
        var items = await _client.GetSeasonsSeriesAsync(mid, page, pageSize, cancellationToken).ConfigureAwait(false)
            ?? throw new BilibiliApiResponseException(nameof(LoadSeasonsSeriesPageAsync), "Collection response did not contain its payload.");
        return new UserSpaceSeasonsPage(items, Math.Max(page, (int)Math.Ceiling((double)items.Page.Total / pageSize)));
    }

    internal static IReadOnlyList<UserSpaceFavoriteFolder> MapFavoriteFolders(IReadOnlyList<FavoritesMetaInfo>? favorites)
    {
        return favorites == null ? [] : favorites.Where(item => item.MediaCount > 0)
            .Select(item => new UserSpaceFavoriteFolder(item.Id,
                string.IsNullOrWhiteSpace(item.Cover) ? "avares://DownKyi.Desktop/Resources/video-placeholder.png" : item.Cover,
                item.Title, item.MediaCount, item.Mtime)).ToArray();
    }
}
