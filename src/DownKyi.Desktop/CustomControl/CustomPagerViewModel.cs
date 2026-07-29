using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DownKyi.CustomControl;

internal sealed partial class CustomPagerViewModel : ObservableObject
{
    private int _count;
    private int _current;
    private bool? _visibility;

    public CustomPagerViewModel(int current, int count)
    {
        _count = Math.Max(0, count);
        _current = _count == 0
            ? 1
            : Math.Clamp(current, 1, _count);
        _visibility = _count > 1;
        ApplyLayout();
    }

    public event EventHandler<CancelEventArgs>? CurrentChanging;

    public int ProposedCurrent { get; private set; }

    public bool? Visibility
    {
        get => _visibility;
        set => SetProperty(ref _visibility, value);
    }

    public int Count
    {
        get => _count;
        set
        {
            if (value < Current || value < 0)
            {
                Visibility = false;
                return;
            }

            _count = value;
            Visibility = _count > 1;
            ApplyLayout();
            OnPropertyChanged();
        }
    }

    public int Current
    {
        get => _current;
        set
        {
            if ((_count > 0 && (value > _count || value < 1))
                || !RequestCurrentChange(value))
            {
                return;
            }

            _current = value;
            ApplyLayout();
            OnPropertyChanged();
        }
    }

    private bool RequestCurrentChange(int current)
    {
        ProposedCurrent = current;
        if (CurrentChanging == null)
        {
            return true;
        }

        var eventArgs = new CancelEventArgs();
        CurrentChanging.Invoke(this, eventArgs);
        return !eventArgs.Cancel;
    }

    private void ApplyLayout()
    {
        var layout = PagerLayout.Create(_current, _count);
        First = layout.First;
        PreviousSecond = layout.PreviousSecond;
        PreviousFirst = layout.PreviousFirst;
        NextFirst = layout.NextFirst;
        NextSecond = layout.NextSecond;
        PreviousVisibility = layout.PreviousVisibility;
        FirstVisibility = layout.FirstVisibility;
        LeftJumpVisibility = layout.LeftJumpVisibility;
        PreviousSecondVisibility = layout.PreviousSecondVisibility;
        PreviousFirstVisibility = layout.PreviousFirstVisibility;
        NextFirstVisibility = layout.NextFirstVisibility;
        NextSecondVisibility = layout.NextSecondVisibility;
        RightJumpVisibility = layout.RightJumpVisibility;
        LastVisibility = layout.LastVisibility;
        NextVisibility = layout.NextVisibility;
    }
}
