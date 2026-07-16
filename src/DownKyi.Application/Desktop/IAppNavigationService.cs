namespace DownKyi.Application.Desktop;

public enum AppNavigationRegion
{
    Main = 0,
    Settings = 1,
    DownloadManager = 2,
    Friends = 3,
    UserSpace = 4,
    Toolbox = 5
}

public enum AppRoute
{
    Index = 0,
    Login = 1,
    VideoDetail = 2,
    Settings = 3,
    Toolbox = 4,
    DownloadManager = 5,
    PublicFavorites = 6,
    UserSpace = 7,
    Publication = 8,
    SeasonsSeries = 9,
    Friends = 10,
    MySpace = 11,
    MyFavorites = 12,
    MyBangumiFollow = 13,
    MyToViewVideo = 14,
    MyHistory = 15,
    Downloading = 16,
    DownloadFinished = 17,
    Following = 18,
    Follower = 19,
    SettingsBasic = 20,
    SettingsNetwork = 21,
    SettingsVideo = 22,
    SettingsDanmaku = 23,
    SettingsAbout = 24,
    BiliHelper = 25,
    Delogo = 26,
    ExtractMedia = 27,
    Archive = 28,
    UserSpaceChannel = 29,
    UserSpaceSeasonsSeries = 30
}

public sealed record AppNavigationRequest(
    AppRoute Route,
    AppRoute? Parent = null,
    object? Parameter = null);

public interface IAppNavigationService
{
    void Navigate(AppNavigationRequest request);

    void NavigateRegion(
        AppNavigationRegion region,
        AppRoute route,
        IReadOnlyDictionary<string, object?>? parameters = null);

    void ClearRegion(AppNavigationRegion region);

    object? GetActiveView(AppNavigationRegion region);
}
