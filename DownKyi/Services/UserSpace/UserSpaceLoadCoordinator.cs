using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
    public static Task<UserSpaceSnapshot> LoadAsync(
        ISettingsStore settingsStore,
        long mid,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var settings = Core.BiliApi.Users.UserSpace.GetSpaceSettings(mid);
            var user = UserInfo.GetUserInfoForSpace(settingsStore, mid);
            cancellationToken.ThrowIfCancellationRequested();
            var publicationTypes = Core.BiliApi.Users.UserSpace.GetPublicationType(settingsStore, mid);
            cancellationToken.ThrowIfCancellationRequested();
            var seasonsSeries = Core.BiliApi.Users.UserSpace.GetSeasonsSeries(mid, 1, 20);
            cancellationToken.ThrowIfCancellationRequested();
            var relation = UserStatus.GetUserRelationStat(mid);
            cancellationToken.ThrowIfCancellationRequested();
            var statistics = UserStatus.GetUpStat(mid);
            cancellationToken.ThrowIfCancellationRequested();
            return new UserSpaceSnapshot(
                settings,
                user,
                publicationTypes,
                seasonsSeries,
                relation,
                statistics);
        }, cancellationToken);
    }
}
