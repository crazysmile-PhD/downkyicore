using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;

namespace DownKyi.CustomControl;

internal sealed class DownloadPreparationStatus : TemplatedControl
{
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<DownloadPreparationStatus, bool>(nameof(IsActive));

    public static readonly StyledProperty<ICommand?> CancelCommandProperty =
        AvaloniaProperty.Register<DownloadPreparationStatus, ICommand?>(nameof(CancelCommand));

    public static readonly StyledProperty<Orientation> OrientationProperty =
        AvaloniaProperty.Register<DownloadPreparationStatus, Orientation>(nameof(Orientation));

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public ICommand? CancelCommand
    {
        get => GetValue(CancelCommandProperty);
        set => SetValue(CancelCommandProperty, value);
    }

    public Orientation Orientation
    {
        get => GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }
}
