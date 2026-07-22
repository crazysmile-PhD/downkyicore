using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Core.BiliApi.Login;
using DownKyi.Core.Logging;
using DownKyi.Core.Settings;
using DownKyi.Images;
using DownKyi.Services.UserSpace;
using DownKyi.Utils;
using DownKyi.ViewModels.PageViewModels;
using Microsoft.Extensions.Logging;

namespace DownKyi.ViewModels;

internal class ViewMySpaceViewModel : ViewModelBase
{
    public const string Tag = "PageMySpace";

    private readonly IUserSpacePageCoordinator _userSpaceCoordinator;
    private readonly ILogger<ViewMySpaceViewModel> _logger;
    private readonly ISettingsStore _settingsStore;
    private CancellationTokenSource? _loadCancellation;

    // mid
    private long _mid = -1;

    #region 页面属性申明

    private VectorImage _arrowBack = null!;

    public VectorImage ArrowBack
    {
        get => _arrowBack;
        set => SetProperty(ref _arrowBack, value);
    }

    private VectorImage _logout = null!;

    public VectorImage Logout
    {
        get => _logout;
        set => SetProperty(ref _logout, value);
    }

    private bool _loading;

    public bool Loading
    {
        get => _loading;
        set => SetProperty(ref _loading, value);
    }

    private bool _noDataVisibility;

    public bool NoDataVisibility
    {
        get => _noDataVisibility;
        set => SetProperty(ref _noDataVisibility, value);
    }

    private bool _loadingVisibility;

    public bool LoadingVisibility
    {
        get => _loadingVisibility;
        set => SetProperty(ref _loadingVisibility, value);
    }

    private bool _viewVisibility;

    public bool ViewVisibility
    {
        get => _viewVisibility;
        set => SetProperty(ref _viewVisibility, value);
    }

    private bool _contentVisibility;

    public bool ContentVisibility
    {
        get => _contentVisibility;
        set => SetProperty(ref _contentVisibility, value);
    }

    private string _topNavigationBg = string.Empty;

    public string TopNavigationBg
    {
        get => _topNavigationBg;
        set => SetProperty(ref _topNavigationBg, value);
    }

    private string? _background;

    public string? Background
    {
        get => _background;
        set => SetProperty(ref _background, value);
    }

    private string? _header;

    public string? Header
    {
        get => _header;
        set => SetProperty(ref _header, value);
    }

    private string _userName = string.Empty;

    public string UserName
    {
        get => _userName;
        set => SetProperty(ref _userName, value);
    }

    private Bitmap? _sex;

    public Bitmap? Sex
    {
        get => _sex;
        set => SetProperty(ref _sex, value);
    }

    private Bitmap? _level;

    public Bitmap? Level
    {
        get => _level;
        set => SetProperty(ref _level, value);
    }

    private bool _vipTypeVisibility;

    public bool VipTypeVisibility
    {
        get => _vipTypeVisibility;
        set => SetProperty(ref _vipTypeVisibility, value);
    }

    private string _vipType = string.Empty;

    public string VipType
    {
        get => _vipType;
        set => SetProperty(ref _vipType, value);
    }

    private string _sign = string.Empty;

    public string Sign
    {
        get => _sign;
        set => SetProperty(ref _sign, value);
    }

    private VectorImage _coinIcon = null!;

    public VectorImage CoinIcon
    {
        get => _coinIcon;
        set => SetProperty(ref _coinIcon, value);
    }

    private string _coin = string.Empty;

    public string Coin
    {
        get => _coin;
        set => SetProperty(ref _coin, value);
    }

    private VectorImage _moneyIcon = null!;

    public VectorImage MoneyIcon
    {
        get => _moneyIcon;
        set => SetProperty(ref _moneyIcon, value);
    }

    private string _money = string.Empty;

    public string Money
    {
        get => _money;
        set => SetProperty(ref _money, value);
    }

    private VectorImage _bindingEmail = null!;

    public VectorImage BindingEmail
    {
        get => _bindingEmail;
        set => SetProperty(ref _bindingEmail, value);
    }

    private bool _bindingEmailVisibility;

    public bool BindingEmailVisibility
    {
        get => _bindingEmailVisibility;
        set => SetProperty(ref _bindingEmailVisibility, value);
    }

    private VectorImage _bindingPhone = null!;

    public VectorImage BindingPhone
    {
        get => _bindingPhone;
        set => SetProperty(ref _bindingPhone, value);
    }

    private bool _bindingPhoneVisibility;

    public bool BindingPhoneVisibility
    {
        get => _bindingPhoneVisibility;
        set => SetProperty(ref _bindingPhoneVisibility, value);
    }

    private string _levelText = string.Empty;

    public string LevelText
    {
        get => _levelText;
        set => SetProperty(ref _levelText, value);
    }

    private string _currentExp = string.Empty;

    public string CurrentExp
    {
        get => _currentExp;
        set => SetProperty(ref _currentExp, value);
    }

    private int _expProgress;

    public int ExpProgress
    {
        get => _expProgress;
        set => SetProperty(ref _expProgress, value);
    }

    private int _maxExp;

    public int MaxExp
    {
        get => _maxExp;
        set => SetProperty(ref _maxExp, value);
    }

    private ObservableCollection<SpaceItem> _statusList = new();

    public ObservableCollection<SpaceItem> StatusList
    {
        get => _statusList;
        private set => SetProperty(ref _statusList, value);
    }

    private ObservableCollection<SpaceItem> _packageList = new();

    public ObservableCollection<SpaceItem> PackageList
    {
        get => _packageList;
        private set => SetProperty(ref _packageList, value);
    }

    private int _selectedStatus = -1;

    public int SelectedStatus
    {
        get => _selectedStatus;
        set => SetProperty(ref _selectedStatus, value);
    }

    private int _selectedPackage = -1;

    public int SelectedPackage
    {
        get => _selectedPackage;
        set => SetProperty(ref _selectedPackage, value);
    }

    #endregion

    public ViewMySpaceViewModel(
        IDesktopInteractionContext desktopInteractions,
        IUserSpacePageCoordinator userSpaceCoordinator,
        ISettingsStore settingsStore,
        ILogger<ViewMySpaceViewModel> logger) : base(desktopInteractions)
    {
        _userSpaceCoordinator = userSpaceCoordinator ?? throw new ArgumentNullException(nameof(userSpaceCoordinator));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        #region 属性初始化

        // 返回按钮
        ArrowBack = NavigationIcon.CreateArrowBack();
        ArrowBack.Fill = DictionaryResource.GetColor("ColorTextDark");

        // 退出登录按钮
        Logout = NavigationIcon.CreateLogout();
        Logout.Fill = DictionaryResource.GetColor("ColorTextDark");

        // 初始化loading
        Loading = true;

        TopNavigationBg = "#00FFFFFF"; // 透明

        // B站图标
        CoinIcon = NormalIcon.Instance().CoinIcon;
        CoinIcon.Fill = DictionaryResource.GetColor("ColorPrimary");
        MoneyIcon = NormalIcon.Instance().MoneyIcon;
        MoneyIcon.Fill = DictionaryResource.GetColor("ColorMoney");
        BindingEmail = NormalIcon.Instance().BindingEmail;
        BindingEmail.Fill = DictionaryResource.GetColor("ColorPrimary");
        BindingPhone = NormalIcon.Instance().BindingPhone;
        BindingPhone.Fill = DictionaryResource.GetColor("ColorPrimary");

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
                data["friendId"] = 0;
                Navigation.Navigate(new AppNavigationRequest(
                    AppRoute.Friends,
                    AppRoute.MySpace,
                    data));
                break;
            case 2:
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
    private RelayCommand? _packageListCommand;

    public RelayCommand PackageListCommand => _packageListCommand ??= new RelayCommand(ExecutePackageListCommand);

    /// <summary>
    /// 页面选择事件
    /// </summary>
    private void ExecutePackageListCommand()
    {
        if (SelectedPackage == -1)
        {
            return;
        }

        switch (SelectedPackage)
        {
            case 0:
                Navigation.Navigate(new AppNavigationRequest(
                    AppRoute.MyFavorites,
                    AppRoute.MySpace,
                    _mid));
                break;
            case 1:
                Navigation.Navigate(new AppNavigationRequest(
                    AppRoute.MyBangumiFollow,
                    AppRoute.MySpace,
                    _mid));
                break;
            case 2:
                Navigation.Navigate(new AppNavigationRequest(
                    AppRoute.MyToViewVideo,
                    AppRoute.MySpace,
                    _mid));
                break;
            case 3:
                Navigation.Navigate(new AppNavigationRequest(
                    AppRoute.MyHistory,
                    AppRoute.MySpace,
                    _mid));
                break;
            default:
                break;
        }

        SelectedPackage = -1;
    }

    #endregion

    /// <summary>
    /// 初始化页面
    /// </summary>
    private void InitView()
    {
        TopNavigationBg = "#00FFFFFF"; // 透明
        ArrowBack.Fill = DictionaryResource.GetColor("ColorTextDark");
        Logout.Fill = DictionaryResource.GetColor("ColorTextDark");
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
        StatusList.Add(new SpaceItem { IsEnabled = true, Title = DictionaryResource.GetString("Whisper"), Subtitle = "--" });
        StatusList.Add(new SpaceItem { IsEnabled = true, Title = DictionaryResource.GetString("Follower"), Subtitle = "--" });
        StatusList.Add(new SpaceItem { IsEnabled = false, Title = DictionaryResource.GetString("Black"), Subtitle = "--" });
        StatusList.Add(new SpaceItem { IsEnabled = false, Title = DictionaryResource.GetString("Moral"), Subtitle = "--" });
        StatusList.Add(new SpaceItem { IsEnabled = false, Title = DictionaryResource.GetString("Silence"), Subtitle = "N/A" });

        PackageList.Clear();
        PackageList.Add(new SpaceItem
        {
            IsEnabled = true,
            Image = NormalIcon.Instance().FavoriteOutline,
            Title = DictionaryResource.GetString("Favorites")
        });
        PackageList.Add(new SpaceItem
        {
            IsEnabled = true,
            Image = NormalIcon.Instance().Subscription,
            Title = DictionaryResource.GetString("Subscription")
        });
        PackageList.Add(new SpaceItem
        {
            IsEnabled = true,
            Image = NormalIcon.Instance().ToView,
            Title = DictionaryResource.GetString("ToView")
        });
        PackageList.Add(new SpaceItem
        {
            IsEnabled = true,
            Image = NormalIcon.Instance().History,
            Title = DictionaryResource.GetString("History")
        });
        NormalIcon.Instance().FavoriteOutline.Fill = DictionaryResource.GetColor("ColorPrimary");
        NormalIcon.Instance().Subscription.Fill = DictionaryResource.GetColor("ColorPrimary");
        NormalIcon.Instance().ToView.Fill = DictionaryResource.GetColor("ColorPrimary");
        NormalIcon.Instance().History.Fill = DictionaryResource.GetColor("ColorPrimary");

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
        StatusList[4].Subtitle = profile.Moral;
        StatusList[5].Subtitle = profile.Silence;

        ArrowBack.Fill = DictionaryResource.GetColor("ColorText");
        Logout.Fill = DictionaryResource.GetColor("ColorText");
        TopNavigationBg = DictionaryResource.GetColor("ColorMask100");
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
        StatusList[1].Subtitle = stats.Whisper ?? StatusList[1].Subtitle;
        StatusList[2].Subtitle = stats.Follower ?? StatusList[2].Subtitle;
        StatusList[3].Subtitle = stats.Black ?? StatusList[3].Subtitle;
    }

    private void ShowNoData()
    {
        TopNavigationBg = "#00FFFFFF";
        ArrowBack.Fill = DictionaryResource.GetColor("ColorTextDark");
        Logout.Fill = DictionaryResource.GetColor("ColorTextDark");
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
