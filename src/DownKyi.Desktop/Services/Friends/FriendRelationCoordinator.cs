using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Bilibili;
using DownKyi.Core.BiliApi.Users;
using DownKyi.Core.BiliApi.Users.Models;

namespace DownKyi.Services.Friends;

internal enum FollowingListKind
{
    All,
    Group
}

internal sealed record FollowingOverview(
    UserRelationStat? Relation,
    IReadOnlyList<FollowingGroup> Groups);

internal interface IFriendRelationCoordinator
{
    Task<FollowingOverview> LoadFollowingOverviewAsync(
        long mid,
        bool includePrivateGroups,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<RelationFollowInfo>> LoadFollowingPageAsync(
        long mid,
        FollowingListKind kind,
        long tagId,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<RelationFollow?> LoadFollowerPageAsync(
        long mid,
        int page,
        int pageSize,
        CancellationToken cancellationToken);
}

internal sealed class FriendRelationCoordinator : IFriendRelationCoordinator
{
    private readonly IBilibiliApiClient _client;

    public FriendRelationCoordinator(IBilibiliApiClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<FollowingOverview> LoadFollowingOverviewAsync(
        long mid,
        bool includePrivateGroups,
        CancellationToken cancellationToken)
    {
        var relation = await _client.GetUserRelationStatAsync(mid, cancellationToken)
            .ConfigureAwait(false);
        var groups = includePrivateGroups
            ? await _client.GetFollowingGroupAsync(cancellationToken).ConfigureAwait(false)
              ?? Array.Empty<FollowingGroup>()
            : Array.Empty<FollowingGroup>();
        return new FollowingOverview(relation, groups);
    }

    public async Task<IReadOnlyList<RelationFollowInfo>> LoadFollowingPageAsync(
        long mid,
        FollowingListKind kind,
        long tagId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<RelationFollowInfo>? contents;
        switch (kind)
        {
            case FollowingListKind.All:
                var following = await _client.GetFollowingsAsync(
                    mid,
                    page,
                    pageSize,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                contents = following?.List;
                break;
            case FollowingListKind.Group:
                contents = await _client.GetFollowingGroupContentAsync(
                    tagId,
                    page,
                    pageSize,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        return contents ?? Array.Empty<RelationFollowInfo>();
    }

    public Task<RelationFollow?> LoadFollowerPageAsync(
        long mid,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        return _client.GetFollowersAsync(mid, page, pageSize, cancellationToken);
    }
}
