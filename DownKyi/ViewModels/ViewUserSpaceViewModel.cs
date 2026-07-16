using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using DownKyi.Application.Desktop;
using DownKyi.Core.BiliApi.Users;
using DownKyi.Core.BiliApi.Users.Models;
using DownKyi.Core.Settings;
using DownKyi.Core.Storage;
using DownKyi.Core.Utils;
using DownKyi.Images;
using DownKyi.Services.UserSpace;
using DownKyi.Utils;
using DownKyi.ViewModels.UserSpace;
using Microsoft.Extensions.Logging;
using CommunityToolkit.Mvvm.Input;

namespace DownKyi.ViewModels;

internal class ViewUserSpaceViewModel : ViewModelBase
{
    public const string Tag = "PageUserSpace";

    private readonly ILogger<ViewUserSpaceViewModel> _logger;
    private readonly ISettingsStore _settingsStore;
    private CancellationTokenSource? _loadCancellation;

    // mid
    private long mid = -1;

    #region 页面属性申明

    private VectorImage _arrowBack = null!;

    public VectorImage ArrowBack
    {
        get => _arrowBack;
        set => SetProperty(ref _arrowBack, value);
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

    private string _isFollowed = string.Empty;

    public string IsFollowed
    {
        get => _isFollowed;
        set => SetProperty(ref _isFollowed, value);
    }

    private ObservableCollection<TabLeftBanner> _tabLeftBanners = new();

    public ObservableCollection<TabLeftBanner> TabLeftBanners
    {
        get => _tabLeftBanners;
        private set => SetProperty(ref _tabLeftBanners, value);
    }

    private ObservableCollection<TabRightBanner> _tabRightBanners = new();

    public ObservableCollection<TabRightBanner> TabRightBanners
    {
        get => _tabRightBanners;
        private set => SetProperty(ref _tabRightBanners, value);
    }

    private int _selectedRightBanner;

    public int SelectedRightBanner
    {
        get => _selectedRightBanner;
        set => SetProperty(ref _selectedRightBanner, value);
    }

    #endregion

    public ViewUserSpaceViewModel(
        IDesktopInteractionContext desktopInteractions,
        ISettingsStore settingsStore,
        ILogger<ViewUserSpaceViewModel> logger) : base(desktopInteractions)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ObserveRegion(AppNavigationRegion.UserSpace);

        #region 属性初始化

        // 返回按钮
        ArrowBack = NavigationIcon.Instance().ArrowBack;
        ArrowBack.Fill = DictionaryResource.GetColor("ColorTextDark");

        // 初始化loading
        Loading = true;

        TopNavigationBg = "#00FFFFFF"; // 透明

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

        var parameters = new Dictionary<string, object?>
        {
            ["object"] = banner.NavigationData,
            ["mid"] = mid
        };

        switch (banner.Id)
        {
            case 0: // 投稿
                Navigation.NavigateRegion(
                    AppNavigationRegion.UserSpace,
                    AppRoute.Archive,
                    parameters);
                break;
            case 1: // 频道（弃用）
                Navigation.NavigateRegion(
                    AppNavigationRegion.UserSpace,
                    AppRoute.UserSpaceChannel,
                    parameters);
                break;
            case 2: // 合集和列表
                Navigation.NavigateRegion(
                    AppNavigationRegion.UserSpace,
                    AppRoute.UserSpaceSeasonsSeries,
                    parameters);
                break;
        }
    }

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
        TopNavigationBg = "#00FFFFFF"; // 透明
        ArrowBack.Fill = DictionaryResource.GetColor("ColorTextDark");
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
        UserSpaceSnapshot snapshot;
        try
        {
            snapshot = await UserSpaceLoadCoordinator
                .LoadAsync(_settingsStore, mid, cancellationToken)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var userInfo = snapshot.User;

        // 是否获取到数据
        if (userInfo == null)
        {
            TopNavigationBg = "#00FFFFFF"; // 透明
            ArrowBack.Fill = DictionaryResource.GetColor("ColorTextDark");
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
                "男" => new Uri("avares://DownKyi/Resources/sex/male.png"),
                "女" => new Uri("avares://DownKyi/Resources/sex/female.png"),
                _ => null
            };
            // 性别
            Sex = sexUri == null ? null : ImageHelper.LoadFromResource(sexUri);
            // 等级
            Level = ImageHelper.LoadFromResource(new Uri($"avares://DownKyi/Resources/level/lv{userInfo.Level}.png"));
            VipType = userInfo.Vip?.Label?.Text ?? string.Empty;
            VipTypeVisibility = !string.IsNullOrEmpty(VipType);
            Sign = userInfo.Sign;
            IsFollowed = userInfo.IsFollowed
                ? DictionaryResource.GetString("Followed")
                : DictionaryResource.GetString("NotFollowed");

            ArrowBack.Fill = DictionaryResource.GetColor("ColorText");
            TopNavigationBg = DictionaryResource.GetColor("ColorMask100");
            Background = snapshot.Settings != null
                ? $"https://i0.hdslb.com/{snapshot.Settings.Toutu.Limg}"
                : "avares://DownKyi/Resources/backgound/9-绿荫秘境.png";

            ViewVisibility = true;
            LoadingVisibility = false;
            NoDataVisibility = false;
        }

        ContentVisibility = true;

        // 投稿视频
        var publicationTypes = snapshot.PublicationTypes;
        if (publicationTypes is { Count: > 0 })
        {
            TabLeftBanners.Add(new TabLeftBanner
            {
                NavigationData = publicationTypes,
                Id = 0,
                Icon = NormalIcon.Instance().VideoUp,
                IconColor = "#FF02B5DA",
                Title = DictionaryResource.GetString("Publication"),
                IsSelected = true
            });
        }

        // 合集和列表
        var seasonsSeries = snapshot.SeasonsSeries;
        if (seasonsSeries is { Page.Total: > 0 })
        {
            TabLeftBanners.Add(new TabLeftBanner
            {
                NavigationData = seasonsSeries,
                Id = 2,
                Icon = NormalIcon.Instance().Channel,
                IconColor = "#FF23C9ED",
                Title = DictionaryResource.GetString("SeasonsSeries")
            });
        }

        // 收藏夹
        // 订阅

        // 关系状态数
        var relationStat = snapshot.Relation;
        if (relationStat != null)
        {
            TabRightBanners.Add(new TabRightBanner
            {
                Id = 0,
                IsEnabled = true,
                LabelColor = DictionaryResource.GetColor("ColorPrimary"),
                CountColor = DictionaryResource.GetColor("ColorPrimary"),
                Label = DictionaryResource.GetString("FollowingCount"),
                Count = Format.FormatNumber(relationStat.Following)
            });
            TabRightBanners.Add(new TabRightBanner
            {
                Id = 1,
                IsEnabled = true,
                LabelColor = DictionaryResource.GetColor("ColorPrimary"),
                CountColor = DictionaryResource.GetColor("ColorPrimary"),
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
                LabelColor = DictionaryResource.GetColor("ColorTextGrey"),
                CountColor = DictionaryResource.GetColor("ColorTextDark"),
                Label = DictionaryResource.GetString("LikesCount"),
                Count = Format.FormatNumber(upStat.Likes)
            });

            TabRightBanners.Add(new TabRightBanner
            {
                Id = 3,
                IsEnabled = false,
                LabelColor = DictionaryResource.GetColor("ColorTextGrey"),
                CountColor = DictionaryResource.GetColor("ColorTextDark"),
                Label = DictionaryResource.GetString("ArchiveViewCount"),
                Count = Format.FormatNumber(upStat.Archive.View)
            });

            TabRightBanners.Add(new TabRightBanner
            {
                Id = 4,
                IsEnabled = false,
                LabelColor = DictionaryResource.GetColor("ColorTextGrey"),
                CountColor = DictionaryResource.GetColor("ColorTextDark"),
                Label = DictionaryResource.GetString("ArticleViewCount"),
                Count = Format.FormatNumber(upStat.Article.View)
            });
        }
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

        mid = parameter;

        InitView();
        RunFireAndForget(UpdateSpaceInfoAsync(), nameof(UpdateSpaceInfoAsync), _logger);
    }

    public override void OnNavigatedFrom(AppNavigationContext navigationContext)
    {
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
