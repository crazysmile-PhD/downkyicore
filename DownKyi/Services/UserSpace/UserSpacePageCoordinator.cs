using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Core.BiliApi.Users;
using DownKyi.Core.Utils;
using DownKyi.Utils;
using DownKyi.ViewModels.PageViewModels;
using Prism.Events;
using BiliUserSpace = DownKyi.Core.BiliApi.Users.UserSpace;

namespace DownKyi.Services.UserSpace;

internal sealed record MySpaceProfileSnapshot(
    string Background,
    string Header,
    string UserName,
    string? SexResource,
    string LevelResource,
    bool VipVisible,
    string VipType,
    string Sign,
    bool EmailBound,
    bool PhoneBound,
    string LevelText,
    string CurrentExperience,
    int MaximumExperience,
    int ExperienceProgress,
    string Moral,
    string Silence);

internal sealed record MySpaceStatsSnapshot(
    bool ShowBalances,
    string Coin,
    string Money,
    string? Following,
    string? Whisper,
    string? Follower,
    string? Black);

internal interface IUserSpacePageCoordinator
{
    Task<IReadOnlyList<PublicationMedia>> LoadPublicationPageAsync(
        long mid,
        int page,
        int pageSize,
        long typeId,
        IEventAggregator eventAggregator,
        CancellationToken cancellationToken);

    Task<MySpaceProfileSnapshot?> LoadMyProfileAsync(long mid, CancellationToken cancellationToken);

    Task<MySpaceStatsSnapshot> LoadMyStatsAsync(long mid, CancellationToken cancellationToken);
}

internal sealed class UserSpacePageCoordinator : IUserSpacePageCoordinator
{
    public Task<IReadOnlyList<PublicationMedia>> LoadPublicationPageAsync(
        long mid,
        int page,
        int pageSize,
        long typeId,
        IEventAggregator eventAggregator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(eventAggregator);
        return Task.Run<IReadOnlyList<PublicationMedia>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var videos = BiliUserSpace.GetPublication(
                mid,
                page,
                pageSize,
                typeId,
                cancellationToken: cancellationToken)?.Vlist;
            if (videos == null || videos.Count == 0)
            {
                return Array.Empty<PublicationMedia>();
            }

            var result = new List<PublicationMedia>(videos.Count);
            foreach (var video in videos)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(new PublicationMedia(eventAggregator)
                {
                    Avid = video.Aid,
                    Bvid = video.Bvid,
                    Duration = video.Length,
                    Title = video.Title,
                    PlayNumber = video.Play > 0 ? Format.FormatNumber(video.Play) : "--",
                    CreateTime = DateTimeOffset.FromUnixTimeSeconds(video.Created)
                        .ToLocalTime()
                        .ToString("yyyy-MM-dd", CultureInfo.CurrentCulture),
                    CoverUrl = video.Pic
                });
            }

            return result;
        }, cancellationToken);
    }

    public Task<MySpaceProfileSnapshot?> LoadMyProfileAsync(long mid, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var settings = BiliUserSpace.GetSpaceSettings(mid, cancellationToken);
            var info = UserInfo.GetMyInfo(cancellationToken);
            if (info == null)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var nextExperience = info.LevelExp.NextExp;
            return new MySpaceProfileSnapshot(
                settings?.Toutu?.Limg is { Length: > 0 } background
                    ? $"https://i0.hdslb.com/{background}"
                    : "avares://DownKyi/Resources/backgound/9-绿荫秘境.png",
                info.Face,
                info.Name,
                info.Sex switch
                {
                    "男" => "avares://DownKyi/Resources/sex/male.png",
                    "女" => "avares://DownKyi/Resources/sex/female.png",
                    _ => null
                },
                $"avares://DownKyi/Resources/level/lv{info.Level}.png",
                !string.IsNullOrEmpty(info.Vip.Label?.Text),
                info.Vip.Label?.Text ?? string.Empty,
                info.Sign,
                info.EmailStatus != 0,
                info.TelStatus != 0,
                $"{DictionaryResource.GetString("Level")}{info.LevelExp.CurrentLevel}",
                nextExperience == -1
                    ? $"{info.LevelExp.CurrentExp}/--"
                    : $"{info.LevelExp.CurrentExp}/{nextExperience}",
                nextExperience,
                info.LevelExp.CurrentExp,
                info.Moral.ToString(CultureInfo.CurrentCulture),
                DictionaryResource.GetString(info.Silence == 1 ? "Ban" : "Normal"));
        }, cancellationToken);
    }

    public Task<MySpaceStatsSnapshot> LoadMyStatsAsync(long mid, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var navigation = UserInfo.GetUserInfoForNavigation(cancellationToken);
            var showBalances = navigation is { IsLogin: true };
            var coin = showBalances && navigation != null
                ? navigation.Money.ToString("F1", CultureInfo.CurrentCulture)
                : "0.0";
            var money = showBalances && navigation != null
                ? navigation.Wallet.BcoinBalance.ToString("F1", CultureInfo.CurrentCulture)
                : "0.0";

            var relation = UserStatus.GetUserRelationStat(mid, cancellationToken);
            return new MySpaceStatsSnapshot(
                showBalances,
                coin,
                money,
                relation?.Following.ToString(CultureInfo.CurrentCulture),
                relation?.Whisper.ToString(CultureInfo.CurrentCulture),
                relation?.Follower.ToString(CultureInfo.CurrentCulture),
                relation?.Black.ToString(CultureInfo.CurrentCulture));
        }, cancellationToken);
    }
}
