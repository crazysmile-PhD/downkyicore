using DownKyi.Application.Desktop;
using DownKyi.Application.Downloads;
using DownKyi.Core.Settings;
using DownKyi.Domain.Downloads;
using DownKyi.Services.Download;
using DownKyi.ViewModels.DownloadManager;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class DownloadFinishedPagingTests
{
    [Fact]
    public void EnteringPageLoadsPagesAndDeduplicatesByTaskId()
    {
        var firstCursor = new DownloadHistoryCursor(20, new DownloadTaskId("task-b"));
        var coordinator = new RecordingCoordinator(
            _ => Task.FromResult(new DownloadHistoryPage(
                [CreateHistory("task-a", 30), CreateHistory("task-b", 20)],
                firstCursor)),
            _ => Task.FromResult(new DownloadHistoryPage(
                [CreateHistory("task-b", 20), CreateHistory("task-c", 10)],
                null)));
        var state = new DownloadListState();
        using var settings = new TestSettingsStore();
        using var viewModel = CreateViewModel(
            new TestDesktopInteractionContext(), state, settings.Store, coordinator);

        viewModel.OnNavigatedTo(CreateNavigationContext());

        Assert.Equal(["task-b", "task-a"],
            state.Downloaded.Select(item => item.HistoryRecord.Id.Value));
        Assert.Single(coordinator.PageRequests);
        Assert.Null(coordinator.PageRequests[0].Cursor);
        Assert.Equal(100, coordinator.PageRequests[0].PageSize);

        viewModel.LoadMoreCommand.Execute(null);

        Assert.Equal(["task-c", "task-b", "task-a"],
            state.Downloaded.Select(item => item.HistoryRecord.Id.Value));
        Assert.Equal(2, coordinator.PageRequests.Count);
        Assert.Equal(firstCursor, coordinator.PageRequests[1].Cursor);
    }

    [Fact]
    public void LeavingPageCancelsReadAndRejectsItsLateResult()
    {
        var pendingPage = new TaskCompletionSource<DownloadHistoryPage>();
        var coordinator = new RecordingCoordinator(_ => pendingPage.Task);
        var state = new DownloadListState();
        using var settings = new TestSettingsStore();
        using var viewModel = CreateViewModel(
            new TestDesktopInteractionContext(), state, settings.Store, coordinator);

        var navigationContext = CreateNavigationContext();
        viewModel.OnNavigatedTo(navigationContext);
        var request = Assert.Single(coordinator.PageRequests);

        viewModel.OnNavigatedFrom(navigationContext);
        pendingPage.SetResult(new DownloadHistoryPage([CreateHistory("late", 1)], null));

        Assert.True(request.CancellationToken.IsCancellationRequested);
        Assert.Empty(state.Downloaded);
        viewModel.LoadMoreCommand.Execute(null);
        Assert.Single(coordinator.PageRequests);
    }

    [Fact]
    public void ClearCancelsReadResetsPagingAndRejectsItsLateResult()
    {
        var pendingPage = new TaskCompletionSource<DownloadHistoryPage>();
        var state = new DownloadListState();
        state.AddDownloaded(DownloadTaskProjectionMapper.ToDownloadedItem(
            CreateHistory("already-loaded", 2)));
        var coordinator = new RecordingCoordinator(
            _ => pendingPage.Task,
            clearDownloaded: () => state.ClearDownloaded());
        using var settings = new TestSettingsStore();
        using var viewModel = CreateViewModel(
            new AcceptingDesktopInteractionContext(), state, settings.Store, coordinator);

        viewModel.OnNavigatedTo(CreateNavigationContext());
        var request = Assert.Single(coordinator.PageRequests);

        viewModel.ClearAllDownloadedCommand.Execute(null);
        pendingPage.SetResult(new DownloadHistoryPage([CreateHistory("late", 1)], null));

        Assert.True(request.CancellationToken.IsCancellationRequested);
        Assert.Equal(1, coordinator.ClearCount);
        Assert.Empty(state.Downloaded);
        viewModel.LoadMoreCommand.Execute(null);
        Assert.Single(coordinator.PageRequests);
    }

    private static ViewDownloadFinishedViewModel CreateViewModel(
        IDesktopInteractionContext desktopInteractions,
        DownloadListState state,
        ISettingsStore settingsStore,
        IDownloadManagerCoordinator coordinator) =>
        new(
            desktopInteractions,
            state,
            settingsStore,
            coordinator,
            NullLogger<ViewDownloadFinishedViewModel>.Instance);

    private static AppNavigationContext CreateNavigationContext() =>
        new(
            AppNavigationRegion.DownloadManager,
            AppRoute.DownloadFinished,
            AppRoute.DownloadManager,
            null,
            new AppNavigationParameters());

    private static DownloadHistoryRecord CreateHistory(string id, long finishedTimestamp) =>
        new(
            new DownloadTaskId(id),
            1,
            0,
            1,
            "title",
            id,
            "00:01",
            "avc1",
            80,
            "1080P",
            "AAC",
            null,
            [],
            finishedTimestamp,
            "finished",
            null);

    private sealed class RecordingCoordinator : IDownloadManagerCoordinator
    {
        private readonly Queue<Func<CancellationToken, Task<DownloadHistoryPage>>> _pages;
        private readonly Action? _clearDownloaded;

        public RecordingCoordinator(
            Func<CancellationToken, Task<DownloadHistoryPage>> firstPage,
            Func<CancellationToken, Task<DownloadHistoryPage>>? secondPage = null,
            Action? clearDownloaded = null)
        {
            _pages = new Queue<Func<CancellationToken, Task<DownloadHistoryPage>>>();
            _pages.Enqueue(firstPage);
            if (secondPage != null)
            {
                _pages.Enqueue(secondPage);
            }

            _clearDownloaded = clearDownloaded;
        }

        public List<PageRequest> PageRequests { get; } = [];

        public int ClearCount { get; private set; }

        public Task PauseAllAsync(
            IEnumerable<DownloadingItem> items,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ResumeAllAsync(
            IEnumerable<DownloadingItem> items,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ToggleAsync(
            DownloadingItem item,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteAsync(
            DownloadingItem item,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteAllAsync(
            IEnumerable<DownloadingItem> items,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ClearDownloadedAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClearCount++;
            _clearDownloaded?.Invoke();
            return Task.CompletedTask;
        }

        public Task<DownloadHistoryPage> GetDownloadedPageAsync(
            DownloadHistoryCursor? cursor,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            PageRequests.Add(new PageRequest(cursor, pageSize, cancellationToken));
            return _pages.Dequeue()(cancellationToken);
        }

        public Task RemoveDownloadedAsync(
            DownloadedItem item,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<DownloadArtifactOpenResult> OpenVideoAsync(
            DownloadedItem item,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(DownloadArtifactOpenResult.Opened);

        public Task<DownloadArtifactOpenResult> OpenFolderAsync(
            DownloadedItem item,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(DownloadArtifactOpenResult.Opened);
    }

    private sealed record PageRequest(
        DownloadHistoryCursor? Cursor,
        int PageSize,
        CancellationToken CancellationToken);

    private sealed class AcceptingDesktopInteractionContext : IDesktopInteractionContext
    {
        public IUserNotificationService Notifications { get; } = new SilentNotifications();

        public IAppNavigationService Navigation { get; } = new TestNavigationService();

        public IAppDialogService Dialogs { get; } = new AcceptingDialogService();

        private sealed class SilentNotifications : IUserNotificationService
        {
            public event EventHandler<UserNotificationEventArgs>? NotificationRaised;

            public void Show(string message)
            {
                NotificationRaised?.Invoke(this, new UserNotificationEventArgs(message));
            }
        }

        private sealed class AcceptingDialogService : IAppDialogService
        {
            public Task<AppDialogResult> ShowAsync(
                AppDialogRequest request,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(new AppDialogResult(
                    AppDialogOutcome.Accepted,
                    new Dictionary<string, object?>()));
            }
        }
    }
}
