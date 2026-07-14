using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Xaml.Interactivity;
using DownKyi.Core.Logging;

namespace DownKyi.CustomAction;

internal class ScrollIntoViewBehavior : Behavior<DataGrid>
{
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
        base.OnDetaching();
        if (AssociatedObject != null)
        {
            AssociatedObject.SelectionChanged -= OnSelectionChanged;
        }
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _ = OnSelectionChangedAsync();
    }

    private async Task OnSelectionChangedAsync()
    {
        try
        {
            var dataGrid = AssociatedObject;
            var selectedItem = dataGrid?.SelectedItem;
            if (dataGrid == null || selectedItem == null)
            {
                return;
            }

            // 等待UI更新完成
            await Task.Delay(100).ConfigureAwait(true);

            // 使用UI线程异步执行滚动操作
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                // 直接使用DataGrid的ScrollIntoView方法滚动到选中项
                dataGrid.ScrollIntoView(selectedItem, null);
            });
        }
        catch (InvalidOperationException ex)
        {
            LogManager.Error(nameof(ScrollIntoViewBehavior), ex);
        }
        catch (ArgumentException ex)
        {
            LogManager.Error(nameof(ScrollIntoViewBehavior), ex);
        }
    }
}
