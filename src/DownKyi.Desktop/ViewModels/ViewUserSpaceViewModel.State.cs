using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using DownKyi.Images;
using DownKyi.ViewModels.UserSpace;

namespace DownKyi.ViewModels;

internal partial class ViewUserSpaceViewModel
{
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
}
