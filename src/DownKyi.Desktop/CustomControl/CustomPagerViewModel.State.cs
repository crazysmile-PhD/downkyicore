namespace DownKyi.CustomControl;

internal sealed partial class CustomPagerViewModel
{
    private int _first;
    private int _previousSecond;
    private int _previousFirst;
    private int _nextFirst;
    private int _nextSecond;
    private bool _previousVisibility;
    private bool _firstVisibility;
    private bool _leftJumpVisibility;
    private bool _previousSecondVisibility;
    private bool _previousFirstVisibility;
    private bool _nextFirstVisibility;
    private bool _nextSecondVisibility;
    private bool _rightJumpVisibility;
    private bool _lastVisibility;
    private bool _nextVisibility;

    public int First
    {
        get => _first;
        set => SetProperty(ref _first, value);
    }

    public int PreviousSecond
    {
        get => _previousSecond;
        set => SetProperty(ref _previousSecond, value);
    }

    public int PreviousFirst
    {
        get => _previousFirst;
        set => SetProperty(ref _previousFirst, value);
    }

    public int NextFirst
    {
        get => _nextFirst;
        set => SetProperty(ref _nextFirst, value);
    }

    public int NextSecond
    {
        get => _nextSecond;
        set => SetProperty(ref _nextSecond, value);
    }

    public bool PreviousVisibility
    {
        get => _previousVisibility;
        set => SetProperty(ref _previousVisibility, value);
    }

    public bool FirstVisibility
    {
        get => _firstVisibility;
        set => SetProperty(ref _firstVisibility, value);
    }

    public bool LeftJumpVisibility
    {
        get => _leftJumpVisibility;
        set => SetProperty(ref _leftJumpVisibility, value);
    }

    public bool PreviousSecondVisibility
    {
        get => _previousSecondVisibility;
        set => SetProperty(ref _previousSecondVisibility, value);
    }

    public bool PreviousFirstVisibility
    {
        get => _previousFirstVisibility;
        set => SetProperty(ref _previousFirstVisibility, value);
    }

    public bool NextFirstVisibility
    {
        get => _nextFirstVisibility;
        set => SetProperty(ref _nextFirstVisibility, value);
    }

    public bool NextSecondVisibility
    {
        get => _nextSecondVisibility;
        set => SetProperty(ref _nextSecondVisibility, value);
    }

    public bool RightJumpVisibility
    {
        get => _rightJumpVisibility;
        set => SetProperty(ref _rightJumpVisibility, value);
    }

    public bool LastVisibility
    {
        get => _lastVisibility;
        set => SetProperty(ref _lastVisibility, value);
    }

    public bool NextVisibility
    {
        get => _nextVisibility;
        set => SetProperty(ref _nextVisibility, value);
    }
}
