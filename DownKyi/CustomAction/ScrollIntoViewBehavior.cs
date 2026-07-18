using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Xaml.Interactivity;

namespace DownKyi.CustomAction;

internal class ScrollIntoViewBehavior : Behavior<DataGrid>
{
    private int _selectionVersion;

    protected override void OnAttached()
    {
        base.OnAttached();
        if (AssociatedObject != null)
        {
            AssociatedObject.SelectionChanged += OnSelectionChanged;
        }
    }

    protected override void OnDetaching()
    {
        Interlocked.Increment(ref _selectionVersion);
        base.OnDetaching();
        if (AssociatedObject != null)
        {
            AssociatedObject.SelectionChanged -= OnSelectionChanged;
        }
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var selectionVersion = Interlocked.Increment(ref _selectionVersion);
        _ = OnSelectionChangedAsync(selectionVersion);
    }

    private async Task OnSelectionChangedAsync(int selectionVersion)
    {
        try
        {
            var dataGrid = AssociatedObject;
            var selectedItem = dataGrid?.SelectedItem;
            if (dataGrid == null || selectedItem == null)
            {
                return;
            }

            await Task.Delay(100).ConfigureAwait(true);
            if (selectionVersion != Volatile.Read(ref _selectionVersion))
            {
                return;
            }

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (selectionVersion == Volatile.Read(ref _selectionVersion)
                    && ReferenceEquals(dataGrid, AssociatedObject))
                {
                    dataGrid.ScrollIntoView(selectedItem, null);
                }
            });
        }
        catch (InvalidOperationException)
        {
            return;
        }
        catch (ArgumentException)
        {
            return;
        }
    }

}
