using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using DownKyi.ViewModels.PageViewModels;

namespace DownKyi.Views;

internal partial class ViewVideoDetail : UserControl
{
    public ViewVideoDetail()
    {
        InitializeComponent();
        var videoPages = this.FindControl<DataGrid>("NameVideoPages");
        videoPages?.AddHandler(
            PointerPressedEvent,
            OnVideoPagePointerPressed,
            RoutingStrategies.Tunnel);
    }

    private static void OnVideoPagePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not DataGrid dataGrid ||
            e.GetCurrentPoint(dataGrid).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed ||
            e.Source is not Control source ||
            source is ComboBox or CheckBox ||
            source.FindAncestorOfType<ComboBox>() != null ||
            source.FindAncestorOfType<CheckBox>() != null)
        {
            return;
        }

        var row = source.FindAncestorOfType<DataGridRow>();
        if (row?.DataContext is not VideoPage page)
        {
            return;
        }

        if (page.IsSelected)
        {
            dataGrid.SelectedItems.Remove(page);
        }
        else if (!dataGrid.SelectedItems.Contains(page))
        {
            dataGrid.SelectedItems.Add(page);
        }

        page.IsSelected = !page.IsSelected;
        e.Handled = true;
    }
}
