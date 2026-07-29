using CommunityToolkit.Mvvm.Input;

namespace DownKyi.CustomControl;

internal sealed partial class CustomPagerViewModel
{
    private RelayCommand? _previousCommand;
    private RelayCommand? _firstCommand;
    private RelayCommand? _previousSecondCommand;
    private RelayCommand? _previousFirstCommand;
    private RelayCommand? _nextFirstCommand;
    private RelayCommand? _nextSecondCommand;
    private RelayCommand? _lastCommand;
    private RelayCommand? _nextCommand;
    private RelayCommand<object>? _jumpCommand;

    public RelayCommand PreviousCommand =>
        _previousCommand ??= new RelayCommand(() => Current -= 1);

    public RelayCommand FirstCommand =>
        _firstCommand ??= new RelayCommand(() => Current = 1);

    public RelayCommand PreviousSecondCommand =>
        _previousSecondCommand ??= new RelayCommand(() => Current -= 2);

    public RelayCommand PreviousFirstCommand =>
        _previousFirstCommand ??= new RelayCommand(() => Current -= 1);

    public RelayCommand NextFirstCommand =>
        _nextFirstCommand ??= new RelayCommand(() => Current += 1);

    public RelayCommand NextSecondCommand =>
        _nextSecondCommand ??= new RelayCommand(() => Current += 2);

    public RelayCommand LastCommand =>
        _lastCommand ??= new RelayCommand(() => Current = Count);

    public RelayCommand NextCommand =>
        _nextCommand ??= new RelayCommand(() => Current += 1);

    public RelayCommand<object> JumpCommand =>
        _jumpCommand ??= RequiredParameterCommand.Create<object>(ExecuteJump);

    private void ExecuteJump(object parameter)
    {
        if (parameter is string text && int.TryParse(text, out var page))
        {
            Current = page >= _count ? _count : page;
        }
    }
}
