using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Core.BiliApi.Users;
using DownKyi.Core.BiliApi.Users.Models;

namespace DownKyi.Services.Friends;

internal enum FollowingListKind
{
    All,
    Whisper,
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
    public Task<FollowingOverview> LoadFollowingOverviewAsync(
        long mid,
        bool includePrivateGroups,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relation = UserStatus.GetUserRelationStat(mid);
            cancellationToken.ThrowIfCancellationRequested();
            var groups = includePrivateGroups
                ? UserRelation.GetFollowingGroup() ?? Array.Empty<FollowingGroup>()
                : Array.Empty<FollowingGroup>();
            cancellationToken.ThrowIfCancellationRequested();
            return new FollowingOverview(relation, groups);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<RelationFollowInfo>> LoadFollowingPageAsync(
        long mid,
        FollowingListKind kind,
        long tagId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        return Task.Run<IReadOnlyList<RelationFollowInfo>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var contents = kind switch
            {
                FollowingListKind.All => UserRelation.GetFollowings(mid, page, pageSize)?.List,
                FollowingListKind.Whisper => UserRelation.GetWhispers(page, pageSize),
                FollowingListKind.Group => UserRelation.GetFollowingGroupContent(tagId, page, pageSize),
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
            };
            cancellationToken.ThrowIfCancellationRequested();
            return contents ?? Array.Empty<RelationFollowInfo>();
        }, cancellationToken);
    }

    public Task<RelationFollow?> LoadFollowerPageAsync(
        long mid,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = UserRelation.GetFollowers(mid, page, pageSize);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }, cancellationToken);
    }
}
