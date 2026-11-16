using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Application.Diagnostics;
using DownKyi.Application.Settings;
using DownKyi.Images;
using DownKyi.Infrastructure.Bilibili;
using DownKyi.Presentation;
using DownKyi.Services.UserSpace;
using DownKyi.Utils;
using Microsoft.Extensions.Logging;

namespace DownKyi.ViewModels;

internal partial class MySpaceViewModel : ViewModelBase
{
    public const string Tag = "PageMySpace";
    private static readonly Uri BilibiliDynamicsUri = new("https://t.bilibili.com/");

    private readonly IUserSpacePageCoordinator _userSpaceCoordinator;
    private readonly IPlatformLauncher _platformLauncher;
    private readonly ILogger<MySpaceViewModel> _logger;
    private readonly ISettingsStore _settingsStore;
    private CancellationTokenSource? _loadCancellation;

    // mid
    private long _mid = -1;

    public MySpaceViewModel(
        IDesktopInteractionContext desktopInteractions,
        IUserSpacePageCoordinator userSpaceCoordinator,
        IPlatformLauncher platformLauncher,
        ISettingsStore settingsStore,
        ILogger<MySpaceViewModel> logger) : base(desktopInteractions)
    {
        _userSpaceCoordinator = userSpaceCoordinator ?? throw new ArgumentNullException(nameof(userSpaceCoordinator));
        _platformLauncher = platformLauncher ?? throw new ArgumentNullException(nameof(platformLauncher));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        #region 属性初始化

        // 返回按钮
        ArrowBack = NavigationIcon.CreateArrowBack();

        // 退出登录按钮
        Logout = NavigationIcon.CreateLogout();

        // 初始化loading
        Loading = true;

        // B站图标
        CoinIcon = NormalIcon.Current.CoinIcon;
        MoneyIcon = NormalIcon.Current.MoneyIcon;
        BindingEmail = NormalIcon.Current.BindingEmail;
        BindingPhone = NormalIcon.Current.BindingPhone;

        StatusList = new ObservableCollection<SpaceItem>();
        PackageList = new ObservableCollection<SpaceItem>();

        #endregion
    }

    #region 命令申明

    private RelayCommand? _backSpaceCommand;

    public RelayCommand BackSpaceCommand => _backSpaceCommand ??= new RelayCommand(ExecuteBackSpace);

    protected internal override void ExecuteBackSpace()
    {
        CancelAndDispose(ref _loadCancellation);

        if (TryNavigateBack())
        {
            return;
        }

        NavigateToParent();
    }

    private RelayCommand? _logoutCommand;

    public RelayCommand LogoutCommand => _logoutCommand ??= new RelayCommand(ExecuteLogoutCommand);

    /// <summary>
    /// 退出登录事件
    /// </summary>
    private void ExecuteLogoutCommand()
    {
        // 注销
        LoginHelper.Logout(_settingsStore);

        if (!TryNavigateBack())
        {
            NavigateToParent("logout");
        }
    }

    private RelayCommand? _statusListCommand;

    public RelayCommand StatusListCommand => _statusListCommand ??= new RelayCommand(ExecuteStatusListCommand);

    /// <summary>
    /// 页面选择事件
    /// </summary>
    private void ExecuteStatusListCommand()
    {
        if (SelectedStatus == -1)
        {
            return;
        }

        var data = new Dictionary<string, object>
        {
            { "mid", _mid },
            { "friendId", 0 }
        };

        switch (SelectedStatus)
        {
            case 0:
                data["friendId"] = 0;
                Navigation.Navigate(new AppNavigationRequest(
                    AppRoute.Friends,
                    AppRoute.MySpace,
                    data));
                break;
            case 1:
                data["friendId"] = 1;
                Navigation.Navigate(new AppNavigationRequest(
                    AppRoute.Friends,
                    AppRoute.MySpace,
                    data));
                break;
            default:
                break;
        }

        SelectedStatus = -1;
    }

    // 页面选择事件
    private DownKyiAsyncDelegateCommand? _packageListCommand;

    public DownKyiAsyncDelegateCommand PackageListCommand =>
        _packageListCommand ??= new DownKyiAsyncDelegateCommand(
            ExecutePackageListCommandAsync,
            _logger);

    /// <summary>
    /// 页面选择事件
    /// </summary>
    private async Task ExecutePackageListCommandAsync()
    {
        var selectedPackage = SelectedPackage;
        SelectedPackage = -1;
        if (TryGetPackageRoute(selectedPackage, out var route))
        {
            Navigation.Navigate(new AppNavigationRequest(route, AppRoute.MySpace, _mid));
            return;
        }

        if (selectedPackage != 4)
        {
            return;
        }

        if (!await _platformLauncher.OpenUriAsync(BilibiliDynamicsUri).ConfigureAwait(true))
        {
            _logger.LogWarningMessage("The official Bilibili dynamics page could not be opened.");
        }
    }

    private static bool TryGetPackageRoute(int selectedPackage, out AppRoute route)
    {
        switch (selectedPackage)
        {
            case 0:
                route = AppRoute.MyFavorites;
                return true;
            case 1:
                route = AppRoute.MyBangumiFollow;
                return true;
            case 2:
                route = AppRoute.MyToViewVideo;
                return true;
            case 3:
                route = AppRoute.MyHistory;
                return true;
            default:
                route = default;
                return false;
        }
    }

    #endregion

    /// <summary>
    /// 初始化页面
    /// </summary>
    private void InitView()
    {
        Background = null;

        Header = null;
        UserName = "";
        Sex = null;
        Level = null;
        VipTypeVisibility = false;
        VipType = "";
        Sign = "";

        Coin = "0.0";
        Money = "0.0";

        LevelText = "";
        CurrentExp = "--/--";

        StatusList.Clear();
        StatusList.Add(new SpaceItem { IsEnabled = true, Title = DictionaryResource.GetString("Following"), Subtitle = "--" });
        StatusList.Add(new SpaceItem { IsEnabled = true, Title = DictionaryResource.GetString("Follower"), Subtitle = "--" });
        StatusList.Add(new SpaceItem { IsEnabled = false, Title = DictionaryResource.GetString("Black"), Subtitle = "--" });
        StatusList.Add(new SpaceItem { IsEnabled = false, Title = DictionaryResource.GetString("Moral"), Subtitle = "--" });
        StatusList.Add(new SpaceItem { IsEnabled = false, Title = DictionaryResource.GetString("Silence"), Subtitle = "N/A" });

        PackageList.Clear();
        PackageList.Add(new SpaceItem
        {
            IsEnabled = true,
            Image = NormalIcon.Current.FavoriteOutline,
            Title = DictionaryResource.GetString("Favorites")
        });
        PackageList.Add(new SpaceItem
        {
            IsEnabled = true,
            Image = NormalIcon.Current.Subscription,
            Title = DictionaryResource.GetString("Subscription")
        });
        PackageList.Add(new SpaceItem
        {
            IsEnabled = true,
            Image = NormalIcon.Current.ToView,
            Title = DictionaryResource.GetString("ToView")
        });
        PackageList.Add(new SpaceItem
        {
            IsEnabled = true,
            Image = NormalIcon.Current.History,
            Title = DictionaryResource.GetString("History")
        });
        PackageList.Add(new SpaceItem
        {
            IsEnabled = true,
            Image = NormalIcon.Current.Channel,
            Title = DictionaryResource.GetString("BilibiliDynamics")
        });
        SelectedStatus = -1;
        SelectedPackage = -1;

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
        var cancellationToken = ReplaceCancellationSource(ref _loadCancellation);
        try
        {
            var profile = await _userSpaceCoordinator.LoadMyProfileAsync(_mid, cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            if (profile == null)
            {
                ShowNoData();
                return;
            }

            ApplyProfile(profile);

            try
            {
                var stats = await _userSpaceCoordinator.LoadMyStatsAsync(_mid, cancellationToken).ConfigureAwait(true);
                cancellationToken.ThrowIfCancellationRequested();
                ApplyStats(stats);
            }
            catch (Exception e) when (e is HttpRequestException or InvalidOperationException or ArgumentException
                or FormatException or Newtonsoft.Json.JsonException)
            {
                _logger.LogErrorMessage("Personal space section loading failed.", e);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or ArgumentException
            or FormatException or Newtonsoft.Json.JsonException)
        {
            _logger.LogErrorMessage("Personal space loading failed.", e);
            ShowNoData();
        }
    }

    private void ApplyProfile(MySpaceProfileSnapshot profile)
    {
        Header = profile.Header;
        UserName = profile.UserName;
        Sex = profile.SexResource == null ? null : ImageHelper.LoadFromResource(new Uri(profile.SexResource));
        Level = ImageHelper.LoadFromResource(new Uri(profile.LevelResource));
        VipTypeVisibility = profile.VipVisible;
        VipType = profile.VipType;
        Sign = profile.Sign;
        BindingEmailVisibility = profile.EmailBound;
        BindingPhoneVisibility = profile.PhoneBound;
        LevelText = profile.LevelText;
        CurrentExp = profile.CurrentExperience;
        MaxExp = profile.MaximumExperience;
        ExpProgress = profile.ExperienceProgress;
        StatusList[3].Subtitle = profile.Moral;
        StatusList[4].Subtitle = profile.Silence;

        Background = profile.Background;
        ViewVisibility = true;
        LoadingVisibility = false;
        NoDataVisibility = false;
    }

    private void ApplyStats(MySpaceStatsSnapshot stats)
    {
        ContentVisibility = stats.ShowBalances;
        Coin = stats.Coin;
        Money = stats.Money;
        StatusList[0].Subtitle = stats.Following ?? StatusList[0].Subtitle;
        StatusList[1].Subtitle = stats.Follower ?? StatusList[1].Subtitle;
        StatusList[2].Subtitle = stats.Black ?? StatusList[2].Subtitle;
    }

    private void ShowNoData()
    {
        Background = null;
        ViewVisibility = false;
        LoadingVisibility = false;
        NoDataVisibility = true;
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

        _mid = parameter;

        InitView();
        RunFireAndForget(UpdateSpaceInfoAsync(), nameof(UpdateSpaceInfoAsync), _logger);
    }

    public override void OnNavigatedFrom(AppNavigationContext navigationContext)
    {
        CancelAndDispose(ref _loadCancellation);
        LoadingVisibility = false;
        base.OnNavigatedFrom(navigationContext);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            CancelAndDispose(ref _loadCancellation);
        }

        base.Dispose(disposing);
    }
}
