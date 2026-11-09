using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Bilibili;
using DownKyi.Application.Desktop;
using DownKyi.Application.Diagnostics;
using DownKyi.Application.Media;
using DownKyi.Application.Settings;
using DownKyi.Core.BiliApi;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.Users;
using DownKyi.Core.BiliApi.Users.Models;
using DownKyi.Core.Utils;
using DownKyi.Infrastructure.Bilibili;
using DownKyi.Presentation;
using DownKyi.Utils;
using Microsoft.Extensions.Logging;
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
    string? Follower,
    string? Black);

internal sealed record BangumiFollowPageSnapshot(
    IReadOnlyList<BangumiFollowMedia> Medias,
    int PageCount);

internal sealed record PublicationPageSnapshot(
    IReadOnlyList<PublicationMedia> Medias,
    int TotalCount);

internal sealed record DynamicVideoPageSnapshot(
    IReadOnlyList<PublicationMedia> Medias,
    string? NextOffset,
    bool HasMore);

internal interface IUserSpacePageCoordinator
{
    Task<PublicationPageSnapshot> LoadPublicationPageAsync(
        long mid,
        int page,
        int pageSize,
        long typeId,
        string? keyword,
        CancellationToken cancellationToken);

    Task<DynamicVideoPageSnapshot> LoadDynamicVideoPageAsync(
        long mid,
        string? offset,
        CancellationToken cancellationToken);

    Task<MySpaceProfileSnapshot?> LoadMyProfileAsync(long mid, CancellationToken cancellationToken);

    Task<MySpaceStatsSnapshot> LoadMyStatsAsync(long mid, CancellationToken cancellationToken);

    Task<BangumiFollowPageSnapshot> LoadBangumiFollowPageAsync(
        long mid,
        BangumiType type,
        int page,
        int pageSize,
        CancellationToken cancellationToken);
}

internal sealed class UserSpacePageCoordinator : IUserSpacePageCoordinator
{
    private readonly ISettingsStore _settingsStore;
    private readonly IWbiKeyProvider _wbiKeyProvider;
    private readonly ILogger<UserSpacePageCoordinator> _logger;
    private readonly IAppNavigationService _navigationService;
    private readonly IBilibiliApiClient _client;

    public UserSpacePageCoordinator(
        ISettingsStore settingsStore,
        IWbiKeyProvider wbiKeyProvider,
        IAppNavigationService navigationService,
        ILogger<UserSpacePageCoordinator> logger,
        IBilibiliApiClient client)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _wbiKeyProvider = wbiKeyProvider ?? throw new ArgumentNullException(nameof(wbiKeyProvider));
        _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<PublicationPageSnapshot> LoadPublicationPageAsync(
        long mid,
        int page,
        int pageSize,
        long typeId,
        string? keyword,
        CancellationToken cancellationToken)
    {
        var publication = await WbiRequestExecutor.ExecuteAsync(
            _wbiKeyProvider,
            (keys, unixTimeSeconds) => _client.GetPublicationPageAsync(
                keys,
                unixTimeSeconds,
                mid,
                page,
                pageSize,
                LoginHelper.HasSavedLoginInfo,
                typeId,
                keyword: keyword?.Trim() ?? string.Empty,
                cancellationToken: cancellationToken),
            TimeProvider.System,
            cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => MapPublicationPage(
            publication,
            _navigationService,
            _logger,
            cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public async Task<DynamicVideoPageSnapshot> LoadDynamicVideoPageAsync(
        long mid,
        string? offset,
        CancellationToken cancellationToken)
    {
        var page = await _client.GetDynamicPageAsync(mid, offset, cancellationToken)
            .ConfigureAwait(false);
        return await Task.Run(() => MapDynamicVideoPage(
            mid,
            page,
            _navigationService,
            cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    internal static PublicationPageSnapshot MapPublicationPage(
        SpacePublication? publication,
        IAppNavigationService navigationService,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(navigationService);
        ArgumentNullException.ThrowIfNull(logger);
        cancellationToken.ThrowIfCancellationRequested();
        if (publication == null)
        {
            logger.LogWarningMessage("Bilibili publication page could not be loaded.");
            return new PublicationPageSnapshot(Array.Empty<PublicationMedia>(), 0);
        }

        var videos = publication.List.Vlist;
        if (videos == null || videos.Count == 0)
        {
            return new PublicationPageSnapshot(Array.Empty<PublicationMedia>(), publication.Page.Count);
        }

        var result = new List<PublicationMedia>(videos.Count);
        foreach (var video in videos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isSeason = video.IsAvoided != 0
                && video.SeasonId > 0
                && video.Mid > 0;
            var seasonMeta = isSeason ? video.Meta : null;
            var publishTime = seasonMeta is { PublishTime: > 0 }
                ? seasonMeta.PublishTime
                : video.Created;
            result.Add(new PublicationMedia(
                navigationService,
                AppRoute.Publication,
                isSeason ? new SeasonNavigationPayload(video.Mid, video.SeasonId) : null)
            {
                Avid = video.Aid,
                Bvid = video.Bvid,
                Duration = seasonMeta is { EpisodeCount: > 0 }
                    ? seasonMeta.EpisodeCount.ToString(CultureInfo.CurrentCulture)
                    : video.Length,
                Title = !string.IsNullOrWhiteSpace(seasonMeta?.Title)
                    ? seasonMeta.Title
                    : video.Title,
                PlayNumber = isSeason
                    ? DictionaryResource.GetString("SeasonsSeries")
                    : video.Play > 0 ? Format.FormatNumber(video.Play) : "--",
                CreateTime = DateTimeOffset.FromUnixTimeSeconds(publishTime)
                    .ToLocalTime()
                    .ToString("yyyy-MM-dd", CultureInfo.CurrentCulture),
                CoverUrl = !string.IsNullOrWhiteSpace(seasonMeta?.Cover)
                    ? seasonMeta.Cover
                    : video.Pic
            });
        }

        return new PublicationPageSnapshot(result, publication.Page.Count);
    }

    internal static DynamicVideoPageSnapshot MapDynamicVideoPage(
        long mid,
        SpaceDynamicPage page,
        IAppNavigationService navigationService,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(navigationService);
        cancellationToken.ThrowIfCancellationRequested();

        var result = new List<PublicationMedia>();
        foreach (var item in page.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var author = item.Modules?.Author;
            var major = item.Modules?.Dynamic?.Major;
            var archive = major?.Archive;
            if (!string.Equals(item.Type, "DYNAMIC_TYPE_AV", StringComparison.Ordinal)
                || author?.Mid != mid
                || !string.Equals(major?.Type, "MAJOR_TYPE_ARCHIVE", StringComparison.Ordinal)
                || archive == null
                || BilibiliInputParser.Parse(archive.Bvid) is not BilibiliBvidInput)
            {
                continue;
            }

            result.Add(new PublicationMedia(navigationService, AppRoute.Publication)
            {
                Avid = archive.Aid,
                Bvid = archive.Bvid,
                Duration = archive.Duration,
                Title = archive.Title,
                PlayNumber = string.IsNullOrWhiteSpace(archive.Stat?.Play)
                    ? "--"
                    : archive.Stat.Play,
                CreateTime = FormatDynamicPublishedDate(author.PublishedAt),
                CoverUrl = archive.Cover
            });
        }

        return new DynamicVideoPageSnapshot(
            result,
            page.HasMore ? page.Offset : null,
            page.HasMore);
    }

    private static string FormatDynamicPublishedDate(long publishedAt)
    {
        const long minimumUnixSeconds = -62_135_596_800;
        const long maximumUnixSeconds = 253_402_300_799;
        return publishedAt is <= 0 or < minimumUnixSeconds or > maximumUnixSeconds
            ? string.Empty
            : DateTimeOffset.FromUnixTimeSeconds(publishedAt)
                .ToLocalTime()
                .ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);
    }

    public async Task<MySpaceProfileSnapshot?> LoadMyProfileAsync(
        long mid,
        CancellationToken cancellationToken)
    {
        var info = await _client.GetMyInfoAsync(cancellationToken).ConfigureAwait(false);
        if (info == null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var background = await LoadOptionalBackgroundAsync(mid, cancellationToken).ConfigureAwait(false);
        var nextExperience = info.LevelExp.NextExp;
        return new MySpaceProfileSnapshot(
            background,
            info.Face,
            info.Name,
            info.Sex switch
            {
                "男" => "avares://DownKyi.Desktop/Resources/sex/male.png",
                "女" => "avares://DownKyi.Desktop/Resources/sex/female.png",
                _ => null
            },
            $"avares://DownKyi.Desktop/Resources/level/lv{info.Level}.png",
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
    }

    private async Task<string> LoadOptionalBackgroundAsync(
        long mid,
        CancellationToken cancellationToken)
    {
        try
        {
            var publicInfo = await WbiRequestExecutor.ExecuteAsync(
                _wbiKeyProvider,
                (keys, unixTimeSeconds) => _client.GetUserInfoForSpaceAsync(
                    keys,
                    unixTimeSeconds,
                    mid,
                    LoginHelper.HasSavedLoginInfo,
                    cancellationToken),
                TimeProvider.System,
                cancellationToken).ConfigureAwait(false);
            return UserSpaceBackground.Resolve(publicInfo?.TopPhoto);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is BilibiliApiResponseException
            or HttpRequestException
            or OperationCanceledException)
        {
            _logger.LogWarningMessage(
                "Optional user-space background could not be loaded; the profile will use the local background.",
                exception);
            return UserSpaceBackground.FallbackAddress;
        }
    }

    public async Task<MySpaceStatsSnapshot> LoadMyStatsAsync(
        long mid,
        CancellationToken cancellationToken)
    {
        var navigation = await _client.GetUserInfoForNavigationAsync(cancellationToken)
            .ConfigureAwait(false);
        var showBalances = navigation is { IsLogin: true };
        var coin = showBalances && navigation != null
            ? navigation.Money.ToString("F1", CultureInfo.CurrentCulture)
            : "0.0";
        var money = showBalances && navigation != null
            ? navigation.Wallet.BcoinBalance.ToString("F1", CultureInfo.CurrentCulture)
            : "0.0";

        var relation = await _client.GetUserRelationStatAsync(mid, cancellationToken)
            .ConfigureAwait(false);
        return new MySpaceStatsSnapshot(
            showBalances,
            coin,
            money,
            relation?.Following.ToString(CultureInfo.CurrentCulture),
            relation?.Follower.ToString(CultureInfo.CurrentCulture),
            relation?.Black.ToString(CultureInfo.CurrentCulture));
    }

    public async Task<BangumiFollowPageSnapshot> LoadBangumiFollowPageAsync(
        long mid,
        BangumiType type,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var response = await _client.GetBangumiFollowAsync(
            mid,
            type,
            page,
            pageSize,
            cancellationToken).ConfigureAwait(false);
        if (response?.List == null || response.List.Count == 0)
        {
            return new BangumiFollowPageSnapshot(Array.Empty<BangumiFollowMedia>(), 1);
        }

        var medias = new List<BangumiFollowMedia>(response.List.Count);
        foreach (var item in response.List)
        {
            cancellationToken.ThrowIfCancellationRequested();
            medias.Add(new BangumiFollowMedia(_navigationService, AppRoute.MyBangumiFollow)
            {
                MediaId = item.MediaId,
                SeasonId = item.SeasonId,
                Title = item.Title,
                SeasonTypeName = item.SeasonTypeName,
                Area = item.Areas?.Count > 0 ? item.Areas[0].Name : string.Empty,
                Badge = item.Badge,
                Cover = NormalizeImageAddress(item.Cover),
                Evaluate = item.Evaluate,
                IndexShow = item.NewEp?.IndexShow ?? string.Empty,
                Progress = string.IsNullOrEmpty(item.Progress)
                    ? DictionaryResource.GetString("BangumiNotWatched")
                    : item.Progress
            });
        }

        return new BangumiFollowPageSnapshot(
            medias,
            Math.Max(1, (int)Math.Ceiling((double)response.Total / pageSize)));
    }

    private static string NormalizeImageAddress(string? address)
    {
        if (string.IsNullOrEmpty(address))
        {
            return "avares://DownKyi.Desktop/Resources/video-placeholder.png";
        }

        return address.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? address
            : $"https:{address}";
    }
}
