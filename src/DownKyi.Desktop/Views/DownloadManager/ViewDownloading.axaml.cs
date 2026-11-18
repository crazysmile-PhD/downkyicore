using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using DownKyi.ViewModels.DownloadManager;

namespace DownKyi.Views.DownloadManager;

internal partial class ViewDownloading : UserControl
{
    public ViewDownloading()
    {
        InitializeComponent();
        var list = this.FindControl<ListBox>("DownloadingList")!;
        list.AddHandler(KeyDownEvent, OnQueueKeyDown, RoutingStrategies.Tunnel);
        list.AddHandler(PointerPressedEvent, OnQueuePointerPressed, RoutingStrategies.Tunnel);
    }

    private void OnQueuePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not DownloadingViewModel viewModel || sender is not ListBox list
            || e.Source is not Control source
            || source is Button || source.FindAncestorOfType<Button>() != null
            || !e.GetCurrentPoint(list).Properties.IsLeftButtonPressed
            || source.FindAncestorOfType<ListBoxItem>()?.DataContext is not DownloadingItem item) return;
        viewModel.SelectTask(item, e.KeyModifiers.HasFlag(KeyModifiers.Control), e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        list.SelectedItem = item;
        list.Focus();
        e.Handled = true;
    }

    private void OnQueueKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not DownloadingViewModel viewModel || sender is not ListBox list) return;
        if (e.Key == Key.A && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            viewModel.SelectVisibleItems();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            viewModel.ClearSelectionCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key is Key.Down or Key.Up)
        {
            var visible = viewModel.QueueRows.OfType<DownloadingItem>().ToArray();
            if (visible.Length == 0) return;
            var current = Array.IndexOf(visible, list.SelectedItem as DownloadingItem);
            var next = current < 0 ? e.Key == Key.Down ? 0 : visible.Length - 1
                : Math.Clamp(current + (e.Key == Key.Down ? 1 : -1), 0, visible.Length - 1);
            var item = visible[next];
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                viewModel.SelectTask(item, e.KeyModifiers.HasFlag(KeyModifiers.Control), e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            list.SelectedItem = item;
            list.ScrollIntoView(item);
            list.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Space && list.SelectedItem is DownloadingItem item
            && e.Source is not Button && (e.Source as Control)?.FindAncestorOfType<Button>() == null)
        {
            viewModel.SelectTask(item, true, false);
            e.Handled = true;
        }
    }
}
