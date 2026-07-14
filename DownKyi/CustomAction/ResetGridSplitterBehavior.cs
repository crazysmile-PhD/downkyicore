using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Xaml.Interactivity;

namespace DownKyi.CustomAction;

internal sealed class ResetGridSplitterBehavior : Behavior<GridSplitter>
{
    public static readonly StyledProperty<int> ResetVersionProperty =
        AvaloniaProperty.Register<ResetGridSplitterBehavior, int>(nameof(ResetVersion));

    private readonly Dictionary<int, GridLength> _originalColumnWidths = new();
    private readonly Dictionary<int, GridLength> _originalRowHeights = new();
    private Grid? _parentGrid;

    public int ResetVersion
    {
        get => GetValue(ResetVersionProperty);
        set => SetValue(ResetVersionProperty, value);
    }

    protected override void OnAttached()
    {
        base.OnAttached();
        var gridSplitter = AssociatedObject;
        _parentGrid = gridSplitter?.Parent as Grid;
        _originalColumnWidths.Clear();
        _originalRowHeights.Clear();

        if (_parentGrid != null)
        {
            for (int i = 0; i < _parentGrid.ColumnDefinitions.Count; i++)
            {
                _originalColumnWidths[i] = _parentGrid.ColumnDefinitions[i].Width;
            }

            for (int i = 0; i < _parentGrid.RowDefinitions.Count; i++)
            {
                _originalRowHeights[i] = _parentGrid.RowDefinitions[i].Height;
            }
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);
        if (change.Property == ResetVersionProperty)
        {
            ResetGrid();
        }
    }

    private void ResetGrid()
    {
        if (_parentGrid != null)
        {
            foreach (var kvp in _originalColumnWidths)
            {
                _parentGrid.ColumnDefinitions[kvp.Key].Width = kvp.Value;
            }

            foreach (var kvp in _originalRowHeights)
            {
                _parentGrid.RowDefinitions[kvp.Key].Height = kvp.Value;
            }
        }
    }

    protected override void OnDetachedFromVisualTree()
    {
        base.OnDetachedFromVisualTree();
        ResetGrid();
    }
}
