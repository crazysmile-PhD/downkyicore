using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using DownKyi.Images;
using DownKyi.Presentation;

namespace DownKyi.ViewModels;

internal partial class ViewMySpaceViewModel
{
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
}
