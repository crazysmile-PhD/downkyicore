using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DownKyi.Application.Desktop;
using DownKyi.Application.Downloads;
using DownKyi.Application.Lifetime;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Domain.Downloads;
using DownKyi.Infrastructure.Downloads;
using DownKyi.Infrastructure.Time;
using DownKyi.Models;
using DownKyi.Platform;
using DownKyi.Services.Download;
using DownKyi.ViewModels.DownloadManager;
using DownKyi.Views.DownloadManager;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Desktop.Tests;

public sealed class DownloadQueueInteractionTests
{
    [AvaloniaFact]
    public async Task ShutdownDoesNotWaitForAStoppedUiDispatcherOrApplyItsLateCallback()
    {
        using var fixture = new Fixture();
        var directory = Directory.CreateTempSubdirectory("downkyi-projection-shutdown-");
        try
        {
            var clock = new SystemClock();
            using var store = new SqliteDownloadTaskStore(new(Path.Combine(directory.FullName, "downloads.db")), clock);
            var history = DownloadHistoryService.CreateForSharedStore(store);
            using var tasks = new DownloadTaskApplicationService(store, history, clock);
            using var applicationCancellation = new ApplicationCancellation();
            var dispatcher = new SuspendedUiDispatcher();
            var projections = new DownloadTaskProjectionStore(tasks, history, clock, dispatcher,
                applicationCancellation: applicationCancellation);
            await using var lifetime = projections.ConfigureAwait(true);
            var item = Item("BV-shutdown", "Part one", DownloadStatus.WaitForDownload);
            item.DownloadBase.FilePath = Path.Combine(directory.FullName, "episode");
            var changes = 0;
            item.PropertyChanged += (_, _) => changes++;
            await projections.AddDownloadingAsync(item, TestContext.Current.CancellationToken).ConfigureAwait(true);
            await dispatcher.Queued.Task.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            await applicationCancellation.RequestShutdownAsync().ConfigureAwait(true);
            try
            {
                await projections.DisposeAsync().AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken).ConfigureAwait(true);
            }
            finally
            {
                dispatcher.Release();
            }
            Assert.Equal(0, changes);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    private sealed class SuspendedUiDispatcher : IUiDispatcher
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Action? _callback;
        public TaskCompletionSource Queued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task InvokeAsync(Action action)
        {
            _callback = action;
            Queued.SetResult();
            return _released.Task;
        }

        public void Release()
        {
            _callback?.Invoke();
            _released.SetResult();
        }
    }

    [AvaloniaFact]
    public async Task BackgroundCommitsUpdateTheBoundQueueOnItsUiThreadWithoutRebuildingUnchangedModels()
    {
        using var fixture = new Fixture();
        var directory = Directory.CreateTempSubdirectory("downkyi-queue-projection-");
        try
        {
            var clock = new SystemClock();
            using var store = new SqliteDownloadTaskStore(new(Path.Combine(directory.FullName, "downloads.db")), clock);
            var history = DownloadHistoryService.CreateForSharedStore(store);
            using var tasks = new DownloadTaskApplicationService(store, history, clock);
            var projections = new DownloadTaskProjectionStore(tasks, history, clock, new AvaloniaUiDispatcher());
            await using var projectionLifetime = projections.ConfigureAwait(true);
            var item = Item("BV-source-a", "Part one", DownloadStatus.WaitForDownload);
            item.DownloadBase.FilePath = Path.Combine(directory.FullName, "episode");
            await projections.AddDownloadingAsync(item, TestContext.Current.CancellationToken).ConfigureAwait(true);
            await projections.FlushPresentationAsync().ConfigureAwait(true);
            fixture.State.AddDownloading(item);
            fixture.ViewModel.FailedOnly = true;
            fixture.Show();
            var metadata = item.DownloadBase;
            var runtime = item.Downloading;
            var offThreadChanges = 0;
            var metadataChanges = 0;
            item.PropertyChanged += (_, args) =>
            {
                if (!Dispatcher.UIThread.CheckAccess()) Interlocked.Increment(ref offThreadChanges);
                if (args.PropertyName == nameof(DownloadingItem.DownloadBase)) metadataChanges++;
            };
            ((INotifyCollectionChanged)fixture.ViewModel.QueueRows).CollectionChanged += (_, _) =>
            {
                if (!Dispatcher.UIThread.CheckAccess()) Interlocked.Increment(ref offThreadChanges);
            };
            var id = new DownloadTaskId(item.DownloadBase.Id);
            await Task.Run(async () =>
            {
                Assert.True((await tasks.StartAsync(id, TestContext.Current.CancellationToken).ConfigureAwait(false)).IsSuccess);
                Assert.Equal(DownloadPhase.Downloading, projections.GetRequiredSnapshot(id).Phase);
                Assert.True((await tasks.UpdateActivityAsync(id, "video", "Downloading", TestContext.Current.CancellationToken)
                    .ConfigureAwait(false)).IsSuccess);
            }, TestContext.Current.CancellationToken).ConfigureAwait(true);
            await projections.FlushPresentationAsync().ConfigureAwait(true);
            await Task.Run(() => projections.PublishLiveProgress(id, new DownloadProgress(60, 60, 100)),
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            await projections.FlushPresentationAsync().ConfigureAwait(true);
            Assert.Equal(60, item.Progress);
            await Task.Run(async () =>
            {
                Assert.True((await tasks.UpdateActivityAsync(id, "video", "Finishing", TestContext.Current.CancellationToken)
                    .ConfigureAwait(false)).IsSuccess);
                Assert.True((await tasks.FailAsync(id, new DownloadFailure("test.failure", "Synthetic failure", true),
                    TestContext.Current.CancellationToken).ConfigureAwait(false)).IsSuccess);
            }, TestContext.Current.CancellationToken).ConfigureAwait(true);
            await projections.FlushPresentationAsync().ConfigureAwait(true);

            Assert.Equal(0, offThreadChanges);
            Assert.Equal(0, metadataChanges);
            Assert.Same(metadata, item.DownloadBase);
            Assert.Same(runtime, item.Downloading);
            Assert.Equal(60, item.Progress);
            Assert.Equal(DownloadStatus.DownloadFailed, item.Downloading.DownloadStatus);
            Assert.Equal([item], fixture.ViewModel.QueueRows);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }

    [AvaloniaFact]
    public void SourceIdentityCollapseAndSearchKeepSelectionScopePredictable()
    {
        using var fixture = new Fixture();
        var first = Item("BV-source-a", "Part one");
        var second = Item("BV-source-a", "Part two", DownloadStatus.DownloadFailed);
        var unrelated = Item("BV-source-b", "Same display title");
        fixture.State.AddDownloadingRange([first, unrelated, second]);
        var vm = fixture.ViewModel;
        var group = Assert.Single(vm.QueueRows.OfType<DownloadQueueGroup>());
        Assert.Equal([group, first, second, unrelated], vm.QueueRows);
        first.IsSelected = true;
        unrelated.IsSelected = true;
        vm.ToggleGroupCommand.Execute(group);
        Assert.Equal([group, unrelated], vm.QueueRows);
        Assert.False(first.IsSelected);
        Assert.True(unrelated.IsSelected);

        vm.SearchText = "Part two";
        Assert.Equal([group, second], vm.QueueRows);
        Assert.False(unrelated.IsSelected);
        Assert.True(group.IsExpanded);
        vm.SelectVisibleItems();
        Assert.True(second.IsSelected);
        Assert.Equal(1, vm.SelectedCount);

        vm.SearchText = string.Empty;
        Assert.False(group.IsExpanded);
        Assert.False(second.IsSelected);
        Assert.Equal([group, unrelated], vm.QueueRows);
        Assert.True(group.HasFailures);

        vm.FailedOnly = true;
        Assert.Equal([group, second], vm.QueueRows);
        second.Downloading = new Downloading { DownloadStatus = DownloadStatus.Pause };
        Assert.Empty(vm.QueueRows);
        Assert.True(vm.HasNoMatches);
        Assert.False(vm.CanOperateQueue);
        fixture.Show();
        fixture.Click(fixture.View.FindControl<Button>("NameClearQueueFilters")!);
        Assert.False(vm.HasFilter);
        Assert.Equal([group, unrelated], vm.QueueRows);
    }

    [AvaloniaFact]
    public void TabRecreationPreservesSearchAndIndependentCollapseStates()
    {
        using var fixture = new Fixture();
        fixture.State.AddDownloadingRange([Item("BV-source-a", "Part one"), Item("BV-source-a", "Part two")]);
        var group = Assert.Single(fixture.ViewModel.QueueRows.OfType<DownloadQueueGroup>());
        fixture.ViewModel.ToggleGroupCommand.Execute(group);
        fixture.ViewModel.SearchText = "Part";
        Assert.True(group.IsExpanded);
        fixture.ViewModel.ToggleGroupCommand.Execute(group);
        fixture.ReopenViewModel();
        Assert.Equal("Part", fixture.ViewModel.SearchText);
        group = Assert.Single(fixture.ViewModel.QueueRows.OfType<DownloadQueueGroup>());
        Assert.False(group.IsExpanded);
        fixture.ViewModel.ClearFiltersCommand.Execute(null);
        Assert.False(group.IsExpanded);
    }

    [AvaloniaFact]
    public void CompletingFirstTaskDoesNotMoveItsSourceBehindLaterSources()
    {
        using var fixture = new Fixture();
        var first = Item("BV-a", "First part");
        var later = Item("BV-b", "Other source");
        var remaining = Item("BV-a", "Remaining part");
        fixture.State.AddDownloadingRange([first, later, remaining]);
        var group = Assert.Single(fixture.ViewModel.QueueRows.OfType<DownloadQueueGroup>());
        fixture.State.RemoveDownloading(first);
        Assert.Equal([group, remaining, later], fixture.ViewModel.QueueRows);
        fixture.ViewModel.ToggleGroupCommand.Execute(group);
        fixture.ReopenViewModel();
        group = Assert.IsType<DownloadQueueGroup>(fixture.ViewModel.QueueRows[0]);
        Assert.Equal("BV-a", group.SourceLabel);
        Assert.False(group.IsExpanded);
        Assert.Equal([group, later], fixture.ViewModel.QueueRows);
    }

    [AvaloniaFact]
    public void ProgressUpdatesDoNotRecreateRowsAndRemainingChildKeepsItsCollapsedHeader()
    {
        using var fixture = new Fixture();
        var first = Item("BV-source-a", "Part one");
        var second = Item("BV-source-a", "Part two");
        fixture.State.AddDownloadingRange([first, second]);
        var group = Assert.Single(fixture.ViewModel.QueueRows.OfType<DownloadQueueGroup>());
        var changes = 0;
        ((INotifyCollectionChanged)fixture.ViewModel.QueueRows).CollectionChanged += (_, _) => changes++;
        first.Progress = 52;
        first.Downloading = new Downloading { DownloadStatus = DownloadStatus.Downloading, Progress = 52 };
        Assert.Equal(0, changes);
        Assert.Same(group, fixture.ViewModel.QueueRows[0]);
        fixture.ViewModel.ToggleGroupCommand.Execute(group);
        fixture.State.RemoveDownloading(second);
        Assert.Equal([group], fixture.ViewModel.QueueRows);
        Assert.False(group.IsExpanded);
    }

    [AvaloniaFact]
    public void GroupCheckboxAndKeyboardSelectionUseOnlyVisibleTasks()
    {
        using var fixture = new Fixture();
        var first = Item("BV-source-a", "Part one");
        var second = Item("BV-source-a", "Part two");
        var other = Item("BV-source-b", "Other source");
        fixture.State.AddDownloadingRange([first, second, other]);
        fixture.Show();
        var group = Assert.Single(fixture.ViewModel.QueueRows.OfType<DownloadQueueGroup>());
        var groupContainer = Assert.IsType<ListBoxItem>(fixture.List.ContainerFromIndex(0));
        fixture.Click(Named<Button>(groupContainer, "NameGroupDisclosure"));
        Assert.False(group.IsExpanded);
        fixture.Click(Named<CheckBox>(groupContainer, "NameGroupSelection"));
        Assert.True(group.IsExpanded);
        Assert.True(first.IsSelected);
        Assert.True(second.IsSelected);
        Assert.False(other.IsSelected);

        var firstContainer = Assert.IsType<ListBoxItem>(fixture.List.ContainerFromItem(first));
        fixture.Click(Named<CheckBox>(firstContainer, "NameTaskSelection"));
        Assert.False(first.IsSelected);
        Assert.True(second.IsSelected);
        Assert.Null(group.SelectionState);
        fixture.Click(Named<CheckBox>(groupContainer, "NameGroupSelection"));
        Assert.True(first.IsSelected);
        Assert.True(second.IsSelected);

        fixture.ViewModel.ToggleGroupCommand.Execute(group);
        fixture.Settle();
        Assert.True(fixture.List.Focus());
        fixture.Window.KeyPress(Key.A, RawInputModifiers.Control, PhysicalKey.A, "a");
        fixture.Window.KeyRelease(Key.A, RawInputModifiers.Control, PhysicalKey.A, "a");
        fixture.Settle();
        Assert.False(first.IsSelected);
        Assert.False(second.IsSelected);
        Assert.True(other.IsSelected);
        fixture.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        fixture.Window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(fixture.ViewModel.HasSelection);

        fixture.ViewModel.ToggleGroupCommand.Execute(group);
        fixture.Settle();
        firstContainer = Assert.IsType<ListBoxItem>(fixture.List.ContainerFromItem(first));
        var title = Named<TextBlock>(firstContainer, "NameTaskTitle");
        Assert.Contains("Part one", title.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(first.MainTitle, title.Text, StringComparison.Ordinal);
        fixture.Click(title);
        Assert.True(first.IsSelected);
        fixture.Window.KeyPress(Key.Down, RawInputModifiers.Shift, PhysicalKey.ArrowDown, null);
        fixture.Window.KeyRelease(Key.Down, RawInputModifiers.Shift, PhysicalKey.ArrowDown, null);
        Assert.True(first.IsSelected);
        Assert.True(second.IsSelected);
        Assert.False(other.IsSelected);
    }

    [AvaloniaFact]
    public async Task DeletionConfirmationDoesNotWidenItsTargets()
    {
        using var fixture = new Fixture();
        var first = Item("BV-source-a", "First");
        var second = Item("BV-source-b", "Second");
        fixture.State.AddDownloadingRange([first, second]);
        first.IsSelected = true;
        fixture.ViewModel.DeleteScopeCommand.Execute(null);
        Assert.NotNull(fixture.Dialogs.Request);
        first.IsSelected = false;
        second.IsSelected = true;
        fixture.Dialogs.Complete();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Equal([first], fixture.Coordinator.Deleted);
    }

    [AvaloniaFact]
    public void GroupedQueueKeepsOneVirtualizedViewportAcrossThemesAndSizes()
    {
        using var fixture = new Fixture();
        fixture.State.AddDownloadingRange(Enumerable.Range(0, 1000)
            .Select(index => Item("BV-long-source", $"Part {index}")));
        fixture.Show();
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            fixture.Application.RequestedThemeVariant = theme;
            foreach (var width in new[] { 760, 1024, 1440 })
            {
                fixture.Window.Width = width;
                fixture.Settle();
                Assert.True(fixture.List.GetVisualDescendants().OfType<ListBoxItem>().Count() < 25);
                var toolbar = fixture.View.FindControl<Border>("NameBatchActionBar")!;
                Assert.True(toolbar.Bounds.Bottom <= fixture.View.Bounds.Height);
                var search = fixture.View.FindControl<TextBox>("NameQueueSearch")!;
                var failedOnly = fixture.View.FindControl<ToggleButton>("NameFailedOnly")!;
                Assert.True(search.Bounds.Width > 80);
                Assert.True(search.TranslatePoint(new Point(search.Bounds.Width, 0), fixture.View)!.Value.X
                    < failedOnly.TranslatePoint(default, fixture.View)!.Value.X);
                fixture.State.Downloading[0].IsSelected = true;
                fixture.Settle();
                Assert.False(fixture.View.FindControl<StackPanel>("NameSafeBatchActions")!.IsVisible);
                Assert.True(fixture.View.FindControl<TextBlock>("NameSelectionSummary")!.IsVisible);
                Assert.True(fixture.View.FindControl<Button>("NameDeleteAllButton")!.Bounds.Right <= fixture.View.Bounds.Right);
            }
        }
        fixture.ViewModel.CollapseGroupsCommand.Execute(null);
        fixture.Settle();
        Assert.False(fixture.View.FindControl<CheckBox>("NameSelectVisible")!.IsEnabled);
        fixture.ViewModel.SearchText = "no matching title";
        fixture.Settle();
        Assert.False(fixture.View.FindControl<Button>("NameExpandGroups")!.IsEnabled);
    }

    private static DownloadingItem Item(string bvid, string name, DownloadStatus status = DownloadStatus.Downloading)
    {
        var item = new DownloadingItem
        {
            Downloading = new Downloading
            {
                DownloadStatus = status,
                DownloadStatusTitle = status.ToString(),
                DownloadContent = "视频",
                Progress = 31,
                DownloadingFileSize = "120 MB / 380 MB",
                SpeedDisplay = "2.4 MB/s"
            }
        };
        item.DownloadBase.Bvid = bvid;
        item.DownloadBase.MainTitle = "Same display title";
        item.DownloadBase.Name = name;
        item.DownloadBase.Resolution = new Quality { Name = "1080P" };
        item.DownloadBase.AudioCodec = new Quality { Name = "AAC" };
        return item;
    }

    private static T Named<T>(Control root, string name) where T : Control =>
        Assert.Single(root.GetVisualDescendants().OfType<T>(), control => control.Name == name);

    private sealed class Fixture : IDisposable
    {
        private readonly FluentTheme _theme = new();
        private readonly AvaloniaNavigationService _navigation = new(
            _ => throw new NotSupportedException(), action => action());
        private readonly ThemeVariant? _originalTheme;
        public Fixture()
        {
            Application = DesktopTestResources.EnsureProductThemeResources();
            DesktopTestResources.EnsureDownloadProjectionResources();
            _originalTheme = Application.RequestedThemeVariant;
            Application.Styles.Insert(0, _theme);
            ViewModel = new DownloadingViewModel(new DesktopInteractionContext(new Notifications(), _navigation, Dialogs), State, Coordinator,
                NullLogger<DownloadingViewModel>.Instance);
            View = new ViewDownloading { DataContext = ViewModel };
            List = View.FindControl<ListBox>("DownloadingList")!;
            Window = new Window { Content = View, Width = 800, Height = 620 };
        }
        public Avalonia.Application Application { get; }
        public DownloadListState State { get; } = new();
        public Coordinator Coordinator { get; } = new();
        public Dialogs Dialogs { get; } = new();
        public DownloadingViewModel ViewModel { get; private set; }
        public ViewDownloading View { get; }
        public ListBox List { get; }
        public Window Window { get; }
        public void Show() { Window.Show(); Settle(); }
        public void ReopenViewModel()
        {
            ViewModel.Dispose();
            ViewModel = new DownloadingViewModel(new DesktopInteractionContext(new Notifications(), _navigation, Dialogs),
                State, Coordinator, NullLogger<DownloadingViewModel>.Instance);
            View.DataContext = ViewModel;
        }
        public void Settle() { Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); Window.UpdateLayout(); }
        public void Click(Control control)
        {
            var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), Window)!.Value;
            Window.MouseDown(point, MouseButton.Left);
            Window.MouseUp(point, MouseButton.Left);
            Settle();
        }
        public void Dispose()
        {
            Window.Close(); ViewModel.Dispose();
            _navigation.Dispose();
            Application.Styles.Remove(_theme);
            Application.RequestedThemeVariant = _originalTheme;
        }
    }

    private sealed class Notifications : IUserNotificationService
    {
        public event EventHandler<UserNotificationEventArgs>? NotificationRaised { add { } remove { } }
        public void Show(string message) => throw new NotSupportedException();
    }

    private sealed class Dialogs : IAppDialogService
    {
        private readonly TaskCompletionSource<AppDialogResult> _completion = new();
        public AppDialogRequest? Request { get; private set; }
        public Task<AppDialogResult> ShowAsync(AppDialogRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            return _completion.Task;
        }
        public void Complete() => _completion.SetResult(new AppDialogResult(AppDialogOutcome.Accepted,
            new Dictionary<string, object?>()));
    }

    private sealed class Coordinator : IDownloadManagerCoordinator
    {
        public List<DownloadingItem> Deleted { get; } = [];
        public int GlobalOperations { get; private set; }
        public Task PauseAllAsync(IEnumerable<DownloadingItem> items, CancellationToken cancellationToken = default)
        { GlobalOperations++; return Task.CompletedTask; }
        public Task ResumeAllAsync(IEnumerable<DownloadingItem> items, CancellationToken cancellationToken = default)
        { GlobalOperations++; return Task.CompletedTask; }
        public Task DeleteAllAsync(IEnumerable<DownloadingItem> items, CancellationToken cancellationToken = default)
        { Deleted.AddRange(items); return Task.CompletedTask; }
        public Task ToggleAsync(DownloadingItem item, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(DownloadingItem item, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ClearDownloadedAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task LoadDownloadedHistoryAsync() => throw new NotSupportedException();
        public Task RemoveDownloadedAsync(DownloadedItem item, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DownloadArtifactOpenResult> OpenVideoAsync(DownloadedItem item, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DownloadArtifactOpenResult> OpenFolderAsync(DownloadedItem item, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
