using DownKyi.Application.Desktop;
using DownKyi.Core.BiliApi;
using DownKyi.Core.BiliApi.Users.Models;
using DownKyi.Presentation;
using DownKyi.Services.UserSpace;
using DownKyi.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class UserSpacePageCoordinatorTests
{
    [Fact]
    public async Task PreCanceledPublicationLoadDoesNotStartApiWork()
    {
        using var settings = new TestSettingsStore();
        var coordinator = CreateCoordinator(settings);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.LoadPublicationPageAsync(
            42,
            1,
            30,
            0,
            null,
            cancellation.Token));
    }

    [Fact]
    public void PublicationMappingPreservesServerTotalAndMediaIdentity()
    {
        var publication = new SpacePublication
        {
            Page = new SpacePublicationPage { Count = 35, Pn = 2, Ps = 30 },
            List = new SpacePublicationList
            {
                Vlist =
                [
                    new SpacePublicationListVideo
                    {
                        Aid = 100,
                        Bvid = "BV1fixture01",
                        Title = "fixture",
                        Length = "01:30",
                        Created = 1_700_000_000,
                        Play = 12,
                        Pic = "cover"
                    }
                ]
            }
        };

        var result = UserSpacePageCoordinator.MapPublicationPage(
            publication,
            new TestNavigationService(),
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(35, result.TotalCount);
        Assert.Equal("BV1fixture01", Assert.Single(result.Medias).Bvid);
    }

    [Fact]
    public void AvoidedSeasonRepresentativeNavigatesToExistingCollectionPage()
    {
        var publication = new SpacePublication
        {
            Page = new SpacePublicationPage { Count = 1, Pn = 1, Ps = 30 },
            List = new SpacePublicationList
            {
                Vlist =
                [
                    new SpacePublicationListVideo
                    {
                        Aid = 100,
                        Bvid = "BV1fixture01",
                        Mid = 42,
                        Title = "representative video",
                        Length = "01:30",
                        Created = 1_700_000_000,
                        Pic = "video cover",
                        IsAvoided = 1,
                        SeasonId = 99,
                        Meta = new SpacePublicationListSeasonMeta
                        {
                            Id = 99,
                            Mid = 42,
                            Title = "fixture collection",
                            Cover = "collection cover",
                            EpisodeCount = 26,
                            PublishTime = 1_700_000_100
                        }
                    }
                ]
            }
        };
        var navigation = new TestNavigationService();

        var result = UserSpacePageCoordinator.MapPublicationPage(
            publication,
            navigation,
            NullLogger.Instance,
            CancellationToken.None);

        var media = Assert.Single(result.Medias);
        Assert.False(media.IsDirectVideo);
        Assert.Equal("fixture collection", media.Title);
        Assert.Equal("collection cover", media.CoverUrl);
        Assert.Equal("总时长未知", media.Duration);
        Assert.Equal("共 26 个视频", media.CollectionCountText);
        media.IsSelected = true;
        Assert.False(media.IsSelected);

        media.TitleCommand.Execute(null);

        var request = Assert.Single(navigation.Requests);
        Assert.Equal(AppRoute.SeasonsSeries, request.Route);
        Assert.Equal(AppRoute.Publication, request.Parent);
        var payload = Assert.IsType<SeasonNavigationPayload>(request.Parameter);
        Assert.Equal(42, payload.Mid);
        Assert.Equal(99, payload.SeasonId);

        var directVideo = new PublicationMedia(navigation, AppRoute.Publication)
        {
            Bvid = "BV1direct001",
            IsSelected = true
        };
        var downloadItem = Assert.Single(ViewPublicationViewModel.CreateDownloadItems(
            [media, directVideo]));
        Assert.Equal("BV1direct001", downloadItem.Source);
        Assert.True(downloadItem.IsSelected);
    }

    [Fact]
    public void DynamicVideoMappingKeepsOnlyDirectVideosFromTheRequestedCreator()
    {
        var page = new SpaceDynamicPage
        {
            HasMore = true,
            Offset = "next.offset",
            Items =
            {
                CreateDynamicVideo(42, "DYNAMIC_TYPE_AV", "BV1T6PQzQEUf"),
                CreateDynamicVideo(42, "DYNAMIC_TYPE_FORWARD", "BV17x411w7KC"),
                CreateDynamicVideo(99, "DYNAMIC_TYPE_AV", "BV1xx411c7mD"),
                CreateDynamicVideo(42, "DYNAMIC_TYPE_AV", "invalid")
            }
        };

        var result = UserSpacePageCoordinator.MapDynamicVideoPage(
            42,
            page,
            new TestNavigationService(),
            CancellationToken.None);

        var media = Assert.Single(result.Medias);
        Assert.Equal("BV1T6PQzQEUf", media.Bvid);
        Assert.Equal("dynamic video", media.Title);
        Assert.Equal("1.2万", media.PlayNumber);
        Assert.Equal("03:12", media.Duration);
        Assert.True(result.HasMore);
        Assert.Equal("next.offset", result.NextOffset);
    }

    [Fact]
    public async Task PreCanceledProfileLoadDoesNotStartApiWork()
    {
        using var settings = new TestSettingsStore();
        var coordinator = CreateCoordinator(settings);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.LoadMyProfileAsync(42, cancellation.Token));
    }

    [Fact]
    public async Task ProfileUsesWbiTopPhotoWithoutRequestingLegacySettings()
    {
        const string topPhoto = "http://i0.hdslb.com/bfs/space/header.png";
        var requests = new List<string>();
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (request, _) =>
            {
                requests.Add(request.RequestAddress);
                var response = request.RequestAddress switch
                {
                    var address when address.Contains("/x/space/wbi/acc/info?", StringComparison.Ordinal) =>
                        $$$"""{"code":0,"data":{"mid":42,"top_photo":"{{{topPhoto}}}"}}""",
                    var address when address.Contains("/x/space/myinfo", StringComparison.Ordinal) =>
                        """{"code":0,"data":{"mid":42,"name":"fixture"}}""",
                    _ => throw new InvalidOperationException(
                        $"Unexpected Bilibili request: {request.RequestAddress}")
                };
                return Task.FromResult(response);
            }
        };
        using var settings = new TestSettingsStore();
        var coordinator = CreateCoordinator(settings, client);

        var profile = await coordinator.LoadMyProfileAsync(42, CancellationToken.None);

        Assert.Equal(
            "https://i0.hdslb.com/bfs/space/header.png",
            Assert.IsType<MySpaceProfileSnapshot>(profile).Background);
        Assert.DoesNotContain(
            requests,
            address => address.Contains("/ajax/settings/getSettings", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProfileBackgroundFailureUsesFallbackWithoutHidingProfile()
    {
        var requests = new List<string>();
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (request, _) =>
            {
                requests.Add(request.RequestAddress);
                return request.RequestAddress switch
                {
                    var address when address.Contains("/x/space/myinfo", StringComparison.Ordinal) =>
                        Task.FromResult("""{"code":0,"data":{"mid":42,"name":"fixture"}}"""),
                    var address when address.Contains("/x/space/wbi/acc/info?", StringComparison.Ordinal) =>
                        Task.FromException<string>(new BilibiliApiResponseException("header unavailable")),
                    _ => Task.FromException<string>(new InvalidOperationException(
                        $"Unexpected Bilibili request: {request.RequestAddress}"))
                };
            }
        };
        using var settings = new TestSettingsStore();
        var coordinator = CreateCoordinator(settings, client);

        var profile = await coordinator.LoadMyProfileAsync(42, CancellationToken.None);

        var snapshot = Assert.IsType<MySpaceProfileSnapshot>(profile);
        Assert.Equal("fixture", snapshot.UserName);
        Assert.Equal(UserSpaceBackground.FallbackAddress, snapshot.Background);
        Assert.Contains("/x/space/myinfo", requests[0], StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedBackgroundAddressUsesLocalFallback()
    {
        Assert.Equal(
            UserSpaceBackground.FallbackAddress,
            UserSpaceBackground.Resolve("file://server/share/header.png"));
    }

    [Fact]
    public async Task PreCanceledStatsLoadDoesNotStartApiWork()
    {
        using var settings = new TestSettingsStore();
        var coordinator = CreateCoordinator(settings);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.LoadMyStatsAsync(42, cancellation.Token));
    }

    [Fact]
    public async Task PreCanceledBangumiLoadDoesNotStartApiWork()
    {
        using var settings = new TestSettingsStore();
        var coordinator = CreateCoordinator(settings);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.LoadBangumiFollowPageAsync(
            42,
            BangumiType.ANIME,
            1,
            15,
            cancellation.Token));
    }

    private static UserSpacePageCoordinator CreateCoordinator(
        TestSettingsStore settings,
        TestBilibiliApiClient? client = null)
    {
        return new UserSpacePageCoordinator(
            settings.Store,
            new TestWbiKeyProvider(),
            new TestNavigationService(),
            NullLogger<UserSpacePageCoordinator>.Instance,
            client ?? new TestBilibiliApiClient());
    }

    private static SpaceDynamicItem CreateDynamicVideo(long mid, string type, string bvid)
    {
        return new SpaceDynamicItem
        {
            Type = type,
            Modules = new SpaceDynamicModules
            {
                Author = new SpaceDynamicAuthor
                {
                    Mid = mid,
                    PublishedAt = 1_720_000_000
                },
                Dynamic = new SpaceDynamicContent
                {
                    Major = new SpaceDynamicMajor
                    {
                        Type = "MAJOR_TYPE_ARCHIVE",
                        Archive = new SpaceDynamicArchive
                        {
                            Aid = 100,
                            Bvid = bvid,
                            Title = "dynamic video",
                            Cover = "cover",
                            Duration = "03:12",
                            Stat = new SpaceDynamicArchiveStat { Play = "1.2万" }
                        }
                    }
                }
            }
        };
    }
}
