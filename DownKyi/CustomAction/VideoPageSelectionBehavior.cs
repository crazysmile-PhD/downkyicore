using System.Collections;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Xaml.Interactivity;
using DownKyi.Services.Video;
using DownKyi.ViewModels.PageViewModels;

namespace DownKyi.CustomAction;

internal sealed class VideoPageSelectionBehavior : Behavior<DataGrid>
{
    public static readonly StyledProperty<bool> IsSelectAllProperty =
        AvaloniaProperty.Register<VideoPageSelectionBehavior, bool>(
            nameof(IsSelectAll),
            defaultBindingMode: BindingMode.TwoWay);

    private VideoPage[] _pages = [];
    private bool _isSynchronizing;

    public bool IsSelectAll
    {
        get => GetValue(IsSelectAllProperty);
        set => SetValue(IsSelectAllProperty, value);
    }

    protected override void OnAttached()
    {
        base.OnAttached();
        if (AssociatedObject == null)
        {
            return;
        }

        AssociatedObject.AddHandler(
            InputElement.PointerPressedEvent,
            OnPointerPressed,
            RoutingStrategies.Tunnel);
        AssociatedObject.SelectionChanged += OnSelectionChanged;
        AssociatedObject.PropertyChanged += OnGridPropertyChanged;
        RefreshPages();
    }

    protected override void OnDetaching()
    {
        if (AssociatedObject != null)
        {
            AssociatedObject.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
            AssociatedObject.SelectionChanged -= OnSelectionChanged;
            AssociatedObject.PropertyChanged -= OnGridPropertyChanged;
        }

        UnsubscribePages();
        base.OnDetaching();
    }

    private void OnGridPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == DataGrid.ItemsSourceProperty)
        {
            RefreshPages();
        }
    }

    private void RefreshPages()
    {
        UnsubscribePages();
        _pages = AssociatedObject?.ItemsSource is IEnumerable items
            ? items.Cast<object>().OfType<VideoPage>().ToArray()
            : [];

        foreach (var page in _pages)
        {
            page.PropertyChanged += OnPagePropertyChanged;
        }

        SynchronizeSelectedRows();
    }

    private void UnsubscribePages()
    {
        foreach (var page in _pages)
        {
            page.PropertyChanged -= OnPagePropertyChanged;
        }

        _pages = [];
    }

    private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_isSynchronizing && e.PropertyName == nameof(VideoPage.IsSelected))
        {
            SynchronizeSelectedRows();
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var dataGrid = AssociatedObject;
        if (dataGrid == null ||
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

        page.IsSelected = !page.IsSelected;
        e.Handled = true;
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var dataGrid = AssociatedObject;
        if (_isSynchronizing || dataGrid == null)
        {
            return;
        }

        var visiblePages = dataGrid.ItemsSource is IEnumerable items
            ? items.Cast<object>().OfType<VideoPage>().ToHashSet()
            : [];
        _isSynchronizing = true;
        try
        {
            VideoSelectionState.ApplyVisibleSelectionDelta(
                visiblePages,
                e.RemovedItems.OfType<VideoPage>(),
                e.AddedItems.OfType<VideoPage>());

            SetCurrentValue(
                IsSelectAllProperty,
                visiblePages.Count > 0 && visiblePages.All(page => page.IsSelected));
        }
        finally
        {
            _isSynchronizing = false;
        }
    }

    private void SynchronizeSelectedRows()
    {
        var dataGrid = AssociatedObject;
        if (_isSynchronizing || dataGrid == null)
        {
            return;
        }

        _isSynchronizing = true;
        try
        {
            dataGrid.SelectedItems.Clear();
            foreach (var page in _pages.Where(page => page.IsSelected))
            {
                dataGrid.SelectedItems.Add(page);
            }

            SetCurrentValue(IsSelectAllProperty, _pages.Length > 0 && _pages.All(page => page.IsSelected));
        }
        finally
        {
            _isSynchronizing = false;
        }
    }
}
