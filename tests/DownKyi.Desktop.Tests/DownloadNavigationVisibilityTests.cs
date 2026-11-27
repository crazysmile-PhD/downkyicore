using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DownKyi.Application.Desktop;
using DownKyi.Infrastructure.Settings;
using DownKyi.Platform;
using DownKyi.Services.Download;
using DownKyi.ViewModels;
using DownKyi.ViewModels.DownloadManager;
using DownKyi.Views;
using DownKyi.Views.DownloadManager;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Desktop.Tests;

public sealed class DownloadNavigationVisibilityTests
{
    [AvaloniaFact]
    public void QueueAndHistoryRemainVisibleAcrossCompletionNavigationAndResize()
    {
        var application = DesktopTestResources.EnsureProductThemeResources();
        DesktopTestResources.EnsureDownloadProjectionResources();
        var fluent = new FluentTheme();
        application.Styles.Insert(0, fluent);
        var state = new DownloadListState();
        var coordinator = new Coordinator(state);
        using var settings = new SettingsStore(Path.Combine(Path.GetTempPath(), $"download-visibility-{Guid.NewGuid():N}.json"));
        DesktopInteractionContext? interactions = null;
        using var navigation = new AvaloniaNavigationService(route => route switch
        {
            AppRoute.Downloading => new DownloadingViewModel(interactions!, state, coordinator, NullLogger<DownloadingViewModel>.Instance),
            AppRoute.DownloadFinished => new DownloadFinishedViewModel(interactions!, state, settings, coordinator, NullLogger<DownloadFinishedViewModel>.Instance),
            _ => throw new InvalidOperationException($"Unexpected route {route}")
        }, action => action());
        interactions = new DesktopInteractionContext(new Notifications(), navigation, new Dialogs());
        using var viewModel = new DownloadManagerViewModel(interactions, state, coordinator, NullLogger<DownloadManagerViewModel>.Instance);
        var view = new ViewDownloadManager { DataContext = viewModel };
        view.DataTemplates.Add(new FuncDataTemplate<DownloadingViewModel>((vm, _) => new ViewDownloading { DataContext = vm }));
        view.DataTemplates.Add(new FuncDataTemplate<DownloadFinishedViewModel>((vm, _) => new ViewDownloadFinished { DataContext = vm }));
        var window = new Window { Content = view, Width = 800, Height = 550 };
        try
        {
            viewModel.OnNavigatedTo(new AppNavigationContext(AppNavigationRegion.Main, AppRoute.DownloadManager,
                AppRoute.VideoDetail, null, new AppNavigationParameters()));
            window.Show();
            Settle(window);
            Assert.True(state.IsDownloadedHistoryLoaded);
            AssertCounts(view, "0", "1");
            var pending = new DownloadingItem();
            pending.DownloadBase.Id = "new-task";
            pending.DownloadBase.Name = "New download";
            state.AddDownloading(pending);
            Settle(window);
            AssertCounts(view, "1", "1");
            AssertVisibleItem<ViewDownloading>(view, "DownloadingList", pending);

            var finished = Finished("new-task");
            state.AddDownloaded(finished);
            state.RemoveDownloading(pending);
            Settle(window);
            AssertCounts(view, "0", "2");
            var tabs = view.FindControl<ListBox>("NameLeftTabHeaders")!;
            var historyTab = Assert.IsType<ListBoxItem>(tabs.ContainerFromIndex(1));
            var blank = historyTab.TranslatePoint(new Point(10, 10), window)!.Value;
            window.MouseDown(blank, MouseButton.Left);
            window.MouseUp(blank, MouseButton.Left);
            Settle(window);
            Assert.Equal(1, viewModel.SelectTabId);
            foreach (var size in new[] { new Size(798, 518), new Size(960, 1000), new Size(1920, 1080), new Size(800, 550) })
            {
                window.Width = size.Width;
                window.Height = size.Height;
                Settle(window);
                AssertCounts(view, "0", "2");
                AssertVisibleItem<ViewDownloadFinished>(view, "DownloadedList", finished);
            }
            state.HasMoreDownloadedHistory = true;
            Settle(window);
            AssertCounts(view, "0", "2+");
            var loadMore = view.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "NameLoadMoreHistory");
            Assert.True(loadMore.IsEffectivelyVisible);
            loadMore.Command!.Execute(null);
            Settle(window);
            AssertCounts(view, "0", "3");
            Assert.False(loadMore.IsVisible);
        }
        finally
        {
            window.Close();
            application.Styles.Remove(fluent);
        }
    }

    private static DownloadedItem Finished(string id)
    {
        var item = new DownloadedItem { Downloaded = new DownKyi.Models.Downloaded { FinishedTime = "2026-10-23 12:00:00" } };
        item.DownloadBase.Id = id;
        item.DownloadBase.Name = "Finished download";
        return item;
    }

    private static void AssertVisibleItem<T>(Control root, string listName, object item) where T : Control
    {
        var view = Assert.Single(root.GetVisualDescendants().OfType<T>());
        var list = view.FindControl<ListBox>(listName)!;
        Assert.True(list.IsEffectivelyVisible);
        Assert.Contains(item, list.Items);
        Assert.NotNull(list.ContainerFromItem(item));
        Assert.False(view.FindControl<StackPanel>("DownloadEmptyState")!.IsVisible);
    }

    private static void AssertCounts(Control root, string queue, string history)
    {
        var texts = root.GetVisualDescendants().OfType<TextBlock>().ToArray();
        Assert.Equal(queue, Assert.Single(texts, t => t.Name == "DownloadQueueCount" && t.IsVisible).Text);
        Assert.Equal(history, Assert.Single(texts, t => t.Name == "DownloadHistoryCount" && t.IsVisible).Text);
    }

    private static void Settle(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private sealed class Coordinator(DownloadListState state) : IDownloadManagerCoordinator
    {
        public Task LoadMoreDownloadedHistoryAsync()
        {
            state.AddDownloaded(Finished("next-page"));
            state.HasMoreDownloadedHistory = false;
            return Task.CompletedTask;
        }

        public Task LoadDownloadedHistoryAsync()
        {
            if (!state.IsDownloadedHistoryLoaded) { state.LoadDownloadedHistory([Finished("old-task")]); }
            return Task.CompletedTask;
        }

        public Task PauseAllAsync(IEnumerable<DownloadingItem> items, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ResumeAllAsync(IEnumerable<DownloadingItem> items, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ToggleAsync(DownloadingItem item, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(DownloadingItem item, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAllAsync(IEnumerable<DownloadingItem> items, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ClearDownloadedAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveDownloadedAsync(DownloadedItem item, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DownloadArtifactOpenResult> OpenVideoAsync(DownloadedItem item, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DownloadArtifactOpenResult> OpenFolderAsync(DownloadedItem item, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Notifications : IUserNotificationService
    {
        public event EventHandler<UserNotificationEventArgs>? NotificationRaised { add { } remove { } }
        public void Show(string message) => throw new NotSupportedException();
    }

    private sealed class Dialogs : IAppDialogService
    {
        public Task<AppDialogResult> ShowAsync(AppDialogRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
