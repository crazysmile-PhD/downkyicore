using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Application.Diagnostics;
using DownKyi.Application.Settings;
using DownKyi.Core.BiliApi.Users;
using DownKyi.Core.BiliApi.Users.Models;
using DownKyi.Core.Utils;
using DownKyi.Images;
using DownKyi.Services.UserSpace;
using DownKyi.Utils;
using DownKyi.ViewModels.UserSpace;
using Microsoft.Extensions.Logging;

namespace DownKyi.ViewModels;

internal partial class UserSpaceViewModel : ViewModelBase
{
    public const string Tag = "PageUserSpace";

    private readonly ILogger<UserSpaceViewModel> _logger;
    private readonly ISettingsStore _settingsStore;
    private readonly IUserSpaceLoadCoordinator _loadCoordinator;
    private CancellationTokenSource? _loadCancellation;
    private long? _loadedMid;
    private int _activeTabId = -1;
    private int _activePage = 1;

    // mid
    private long mid = -1;

    public UserSpaceViewModel(
        IDesktopInteractionContext desktopInteractions,
        ISettingsStore settingsStore,
        IUserSpaceLoadCoordinator loadCoordinator,
        ILogger<UserSpaceViewModel> logger) : base(desktopInteractions)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _loadCoordinator = loadCoordinator ?? throw new ArgumentNullException(nameof(loadCoordinator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ObserveRegion(AppNavigationRegion.UserSpace);

        #region 属性初始化

        // 返回按钮
        ArrowBack = NavigationIcon.CreateArrowBack();

        // 初始化loading
        Loading = true;

        TabLeftBanners = new ObservableCollection<TabLeftBanner>();
        TabRightBanners = new ObservableCollection<TabRightBanner>();

        #endregion
    }

    #region 命令申明

    // 返回事件
    private RelayCommand? _backSpaceCommand;

    public RelayCommand BackSpaceCommand => _backSpaceCommand ??= new RelayCommand(ExecuteBackSpace);

    /// <summary>
    /// 返回事件
    /// </summary>
    protected internal override void ExecuteBackSpace()
    {
        if (TryNavigateBack())
        {
            return;
        }

        NavigateToParent();
    }

    // 左侧tab点击事件
    private RelayCommand<object>? _tabLeftBannersCommand;

    public RelayCommand<object> TabLeftBannersCommand => _tabLeftBannersCommand ??= RequiredParameterCommand.Create<object>(ExecuteTabLeftBannersCommand);

    /// <summary>
    /// 左侧tab点击事件
    /// </summary>
    /// <param name="parameter"></param>
    private void ExecuteTabLeftBannersCommand(object parameter)
    {
        if (parameter is not TabLeftBanner banner)
        {
            return;
        }

        if (banner.Id == 1)
        {
            banner.IsSelected = false;
            Navigation.Navigate(new AppNavigationRequest(
                AppRoute.Publication,
                AppRoute.UserSpace,
                new DynamicVideoNavigationPayload(mid)));
            return;
        }

        if (banner.Id is 0 or 2 or 3) NavigateTab(banner.Id);
    }

    private void NavigateTab(int id, int page = 1, bool restore = false)
    {
        if (!restore && id == _activeTabId && Navigation.GetActiveView(AppNavigationRegion.UserSpace) != null) return;
        var lifetime = _loadCancellation?.Token ?? CancellationToken.None;
        if (lifetime.IsCancellationRequested) return;
        _activeTabId = id;
        _activePage = page;
        var parameters = new Dictionary<string, object?>
        {
            ["mid"] = mid,
            ["page"] = page,
            ["lifetime"] = lifetime
        };
        if (id == 0) parameters["object"] = TabLeftBanners.First(banner => banner.Id == 0).NavigationData;
        Navigation.NavigateRegion(AppNavigationRegion.UserSpace, GetTabRoute(id), parameters);
    }

    private static AppRoute GetTabRoute(int id) => id switch
    {
        0 => AppRoute.Archive,
        2 => AppRoute.UserSpaceSeasonsSeries,
        3 => AppRoute.UserSpaceFavorites,
        _ => throw new ArgumentOutOfRangeException(nameof(id))
    };

    // 右侧tab点击事件
    private RelayCommand<object>? _tabRightBannersCommand;

    public RelayCommand<object> TabRightBannersCommand => _tabRightBannersCommand ??= RequiredParameterCommand.Create<object>(ExecuteTabRightBannersCommand);

    /// <summary>
    /// 右侧tab点击事件
    /// </summary>
    private void ExecuteTabRightBannersCommand(object parameter)
    {
        if (!(parameter is TabRightBanner banner))
        {
            return;
        }

        var data = new Dictionary<string, object>
        {
            { "mid", mid },
            { "friendId", 0 }
        };

        var parentRoute = ParentRoute == AppRoute.Friends
            ? AppRoute.Index
            : AppRoute.UserSpace;

        switch (banner.Id)
        {
            case 0:
                data["friendId"] = 0;
                Navigation.Navigate(new AppNavigationRequest(
                    AppRoute.Friends,
                    parentRoute,
                    data));
                break;
            case 1:
                data["friendId"] = 1;
                Navigation.Navigate(new AppNavigationRequest(
                    AppRoute.Friends,
                    parentRoute,
                    data));
                break;
        }

        SelectedRightBanner = -1;
    }

    #endregion

    /// <summary>
    /// 初始化页面
    /// </summary>
    private void InitView()
    {
        _loadedMid = null;
        _activeTabId = -1;
        _activePage = 1;
        Background = null;

        Header = null;
        UserName = "";
        Sex = null;
        Level = null;
        VipTypeVisibility = false;
        VipType = "";
        Sign = "";

        TabLeftBanners.Clear();
        TabRightBanners.Clear();

        SelectedRightBanner = -1;

        // 将内容置空，使其不指向任何页面
        Navigation.ClearRegion(AppNavigationRegion.UserSpace);

        ContentVisibility = false;
        ViewVisibility = false;
        LoadingVisibility = true;
        NoDataVisibility = false;
    }

    /// <summary>
    /// 更新用户信息
    /// </summary>
    private async Task UpdateSpaceInfoAsync()
    {
        var cancellationToken = ResetLoadCancellation();
        var requestedMid = mid;
        try
        {
            var profile = await _loadCoordinator.LoadProfileAsync(requestedMid, cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            ApplyProfile(profile);
            if (profile.User == null)
            {
                _loadedMid = requestedMid;
                return;
            }
            var overview = await _loadCoordinator.LoadOverviewAsync(requestedMid, cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            ApplyOverview(overview);
            _loadedMid = requestedMid;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException
            or ArgumentException or FormatException or Newtonsoft.Json.JsonException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadingVisibility = false;
            _logger.LogErrorMessage("User space could not be loaded.", exception);
            Notifications.Show(DictionaryResource.GetString("UserSpaceLoadFailed"));
        }
    }

    private void ApplyProfile(UserSpaceProfileSnapshot snapshot)
    {
        var userInfo = snapshot.User;

        // 是否获取到数据
        if (userInfo == null)
        {
            Background = null;

            ViewVisibility = false;
            LoadingVisibility = false;
            NoDataVisibility = true;
            return;
        }
        else
        {
            // 头像
            Header = userInfo.Face;
            UserName = userInfo.Name;
            var sexUri = userInfo.Sex switch
            {
                "男" => new Uri("avares://DownKyi.Desktop/Resources/sex/male.png"),
                "女" => new Uri("avares://DownKyi.Desktop/Resources/sex/female.png"),
                _ => null
            };
            // 性别
            Sex = sexUri == null ? null : ImageHelper.LoadFromResource(sexUri);
            // 等级
            Level = ImageHelper.LoadFromResource(new Uri($"avares://DownKyi.Desktop/Resources/level/lv{userInfo.Level}.png"));
            VipType = userInfo.Vip?.Label?.Text ?? string.Empty;
            VipTypeVisibility = !string.IsNullOrEmpty(VipType);
            Sign = userInfo.Sign;
            IsFollowed = userInfo.IsFollowed
                ? DictionaryResource.GetString("Followed")
                : DictionaryResource.GetString("NotFollowed");

            Background = snapshot.Background;

            ViewVisibility = true;
            LoadingVisibility = false;
            NoDataVisibility = false;
        }

    }

    private void ApplyOverview(UserSpaceOverviewSnapshot snapshot)
    {
        TabLeftBanners.Add(new TabLeftBanner
        {
            NavigationData = snapshot.PublicationTypes ?? [],
            Id = 0,
            Icon = NormalIcon.Current.VideoUp,
            IconColor = "#FF02B5DA",
            Title = DictionaryResource.GetString("Publication"),
            IsSelected = true
        });
        TabLeftBanners.Add(new TabLeftBanner
        {
            Id = 1,
            Icon = NormalIcon.Current.Channel1,
            IconColor = "#FF7B61FF",
            Title = DictionaryResource.GetString("DynamicVideos")
        });
        TabLeftBanners.Add(new TabLeftBanner
        {
            Id = 2,
            Icon = NormalIcon.Current.Channel,
            IconColor = "#FF23C9ED",
            Title = DictionaryResource.GetString("SeasonsSeries")
        });
        TabLeftBanners.Add(new TabLeftBanner
        {
            Id = 3,
            Icon = NormalIcon.Current.FavoriteOutline,
            IconColor = "#FFFF6699",
            Title = DictionaryResource.GetString("PublicFavorites")
        });
        // 关系状态数
        var relationStat = snapshot.Relation;
        if (relationStat != null)
        {
            TabRightBanners.Add(new TabRightBanner
            {
                Id = 0,
                IsEnabled = true,
                Label = DictionaryResource.GetString("FollowingCount"),
                Count = Format.FormatNumber(relationStat.Following)
            });
            TabRightBanners.Add(new TabRightBanner
            {
                Id = 1,
                IsEnabled = true,
                Label = DictionaryResource.GetString("FollowerCount"),
                Count = Format.FormatNumber(relationStat.Follower)
            });
        }

        // UP主状态数，需要任意用户登录，否则不会返回任何数据
        var upStat = snapshot.Statistics;
        if (upStat is { Archive: not null, Article: not null })
        {
            TabRightBanners.Add(new TabRightBanner
            {
                Id = 2,
                IsEnabled = false,
                Label = DictionaryResource.GetString("LikesCount"),
                Count = Format.FormatNumber(upStat.Likes)
            });

            TabRightBanners.Add(new TabRightBanner
            {
                Id = 3,
                IsEnabled = false,
                Label = DictionaryResource.GetString("ArchiveViewCount"),
                Count = Format.FormatNumber(upStat.Archive.View)
            });

            TabRightBanners.Add(new TabRightBanner
            {
                Id = 4,
                IsEnabled = false,
                Label = DictionaryResource.GetString("ArticleViewCount"),
                Count = Format.FormatNumber(upStat.Article.View)
            });
        }

        ContentVisibility = true;
        NavigateTab(0);
    }

    /// <summary>
    /// 接收mid参数
    /// </summary>
    /// <param name="navigationContext"></param>
    public override void OnNavigatedTo(AppNavigationContext navigationContext)
    {
        ArgumentNullException.ThrowIfNull(navigationContext);
        base.OnNavigatedTo(navigationContext);

        // 根据传入参数不同执行不同任务
        var parameter = navigationContext.Parameters.GetValue<long>("Parameter");
        if (parameter == 0)
        {
            return;
        }

        if (_loadedMid == parameter)
        {
            var lifetime = ResetLoadCancellation();
            if (_activeTabId >= 0)
            {
                if (Navigation.GetActiveView(AppNavigationRegion.UserSpace) is IUserSpacePageLifetime page
                    && page.Mid == mid && page.Route == GetTabRoute(_activeTabId))
                {
                    page.ResumePage(lifetime);
                }
                else
                {
                    NavigateTab(_activeTabId, _activePage, restore: true);
                }
                foreach (var banner in TabLeftBanners) banner.IsSelected = banner.Id == _activeTabId;
            }
            return;
        }

        mid = parameter;

        InitView();
        RunFireAndForget(UpdateSpaceInfoAsync(), nameof(UpdateSpaceInfoAsync), _logger);
    }

    public override void OnNavigatedFrom(AppNavigationContext navigationContext)
    {
        if (_activeTabId >= 0 && Navigation.GetActiveView(AppNavigationRegion.UserSpace) is IUserSpacePageLifetime page
            && page.Mid == mid && page.Route == GetTabRoute(_activeTabId))
        {
            _activePage = page.Page;
        }
        _loadCancellation?.Cancel();
        base.OnNavigatedFrom(navigationContext);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            _loadCancellation?.Cancel();
            _loadCancellation?.Dispose();
            _loadCancellation = null;
        }

        base.Dispose(disposing);
    }

    private CancellationToken ResetLoadCancellation()
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        return _loadCancellation.Token;
    }
}
