using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.Users;
using DownKyi.Core.BiliApi.Users.Models;
using DownKyi.Core.Settings;

namespace DownKyi.Services.UserSpace;

internal sealed record UserSpaceSnapshot(
    SpaceSettings? Settings,
    UserInfoForSpace? User,
    IReadOnlyList<SpacePublicationListTypeVideoZone>? PublicationTypes,
    SpaceSeasonsSeries? SeasonsSeries,
    UserRelationStat? Relation,
    UpStat? Statistics);

internal static class UserSpaceLoadCoordinator
{
    public static async Task<UserSpaceSnapshot> LoadAsync(
        IWbiKeyProvider wbiKeyProvider,
        long mid,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wbiKeyProvider);
        var user = await WbiRequestExecutor.ExecuteAsync(
            wbiKeyProvider,
            (keys, unixTimeSeconds) => UserInfo.GetUserInfoForSpace(
                keys,
                unixTimeSeconds,
                mid,
                cancellationToken),
            TimeProvider.System,
            cancellationToken).ConfigureAwait(false);
        var publicationTypes = await WbiRequestExecutor.ExecuteAsync(
            wbiKeyProvider,
            (keys, unixTimeSeconds) => Core.BiliApi.Users.UserSpace.GetPublicationType(
                keys,
                unixTimeSeconds,
                mid),
            TimeProvider.System,
            cancellationToken).ConfigureAwait(false);

        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var settings = Core.BiliApi.Users.UserSpace.GetSpaceSettings(mid, cancellationToken);
            var seasonsSeries = Core.BiliApi.Users.UserSpace.GetSeasonsSeries(mid, 1, 20);
            cancellationToken.ThrowIfCancellationRequested();
            var relation = UserStatus.GetUserRelationStat(mid, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var statistics = UserStatus.GetUpStat(mid);
            return new UserSpaceSnapshot(
                settings,
                user,
                publicationTypes,
                seasonsSeries,
                relation,
                statistics);
        }, cancellationToken).ConfigureAwait(false);
    }
}
