using DownKyi.Application.Desktop;
using DownKyi.Application.Downloads;
using DownKyi.Core.Settings;
using DownKyi.Domain.Downloads;
using DownKyi.Services.Download;
using DownKyi.ViewModels.DownloadManager;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class DownloadFinishedHistoryLoadingTests
{
    [Fact]
    public void FirstEntryLoadsAllHistoryOnceAndUsesExistingListSorting()
    {
        var state = new DownloadListState();
        state.AddDownloaded(DownloadTaskProjectionMapper.ToDownloadedItem(
            CreateHistory("task-live", 20)));
        var coordinator = new RecordingCoordinator(
            state,
            () => Task.FromResult<IReadOnlyList<DownloadedItem>>(
            [
                DownloadTaskProjectionMapper.ToDownloadedItem(CreateHistory("task-new", 30)),
                DownloadTaskProjectionMapper.ToDownloadedItem(CreateHistory("task-live", 20)),
                DownloadTaskProjectionMapper.ToDownloadedItem(CreateHistory("task-old", 10))
            ]));
        using var settings = new TestSettingsStore();
        using (var firstViewModel = CreateViewModel(
                   new TestDesktopInteractionContext(), state, settings.Store, coordinator))
        {
            firstViewModel.OnNavigatedTo(CreateNavigationContext());
        }

        using (var secondViewModel = CreateViewModel(
                   new TestDesktopInteractionContext(), state, settings.Store, coordinator))
        {
            secondViewModel.OnNavigatedTo(CreateNavigationContext());
        }

        Assert.Equal(
            ["task-old", "task-live", "task-new"],
            state.Downloaded.Select(item => item.HistoryRecord.Id.Value));
        Assert.True(state.IsDownloadedHistoryLoaded);
        Assert.Equal(2, coordinator.LoadCalls);
        Assert.Equal(1, coordinator.HistoryReads);
    }

    [Fact]
    public async Task LeavingPageDoesNotCancelTheInitialCompleteHistoryLoad()
    {
        var history = new TaskCompletionSource<IReadOnlyList<DownloadedItem>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new DownloadListState();
        var coordinator = new RecordingCoordinator(state, () => history.Task);
        using var settings = new TestSettingsStore();
        using var viewModel = CreateViewModel(
            new TestDesktopInteractionContext(), state, settings.Store, coordinator);
        var navigationContext = CreateNavigationContext();

        viewModel.OnNavigatedTo(navigationContext);
        viewModel.OnNavigatedFrom(navigationContext);
        history.SetResult(
        [
            DownloadTaskProjectionMapper.ToDownloadedItem(CreateHistory("loaded-after-leave", 1))
        ]);
        await coordinator.LastLoad.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(state.IsDownloadedHistoryLoaded);
        Assert.Equal(
            "loaded-after-leave",
            Assert.Single(state.Downloaded).HistoryRecord.Id.Value);
    }

    [Fact]
    public async Task ClearWaitsForInitialHistoryLoadAndDoesNotEnableAReload()
    {
        var history = new TaskCompletionSource<IReadOnlyList<DownloadedItem>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new DownloadListState();
        var coordinator = new RecordingCoordinator(state, () => history.Task);
        using var settings = new TestSettingsStore();
        using var viewModel = CreateViewModel(
            new AcceptingDesktopInteractionContext(), state, settings.Store, coordinator);

        viewModel.OnNavigatedTo(CreateNavigationContext());
        viewModel.ClearAllDownloadedCommand.Execute(null);
        Assert.Equal(0, coordinator.ClearCount);

        history.SetResult(
        [
            DownloadTaskProjectionMapper.ToDownloadedItem(CreateHistory("to-clear", 1))
        ]);
        await coordinator.ClearCalled.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Empty(state.Downloaded);
        Assert.True(state.IsDownloadedHistoryLoaded);
        await coordinator.LoadDownloadedHistoryAsync();
        Assert.Equal(1, coordinator.HistoryReads);
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

    private sealed class RecordingCoordinator(
        DownloadListState state,
        Func<Task<IReadOnlyList<DownloadedItem>>> readHistory) : IDownloadManagerCoordinator
    {
        public int LoadCalls { get; private set; }

        public int HistoryReads { get; private set; }

        public int ClearCount { get; private set; }

        public Task LastLoad { get; private set; } = Task.CompletedTask;

        public TaskCompletionSource ClearCalled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

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
            state.ClearDownloaded();
            ClearCalled.TrySetResult();
            return Task.CompletedTask;
        }

        public Task LoadDownloadedHistoryAsync()
        {
            LoadCalls++;
            if (state.IsDownloadedHistoryLoaded)
            {
                return Task.CompletedTask;
            }

            if (!LastLoad.IsCompleted)
            {
                return LastLoad;
            }

            HistoryReads++;
            LastLoad = LoadCoreAsync();
            return LastLoad;
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

        private async Task LoadCoreAsync()
        {
            state.LoadDownloadedHistory(await readHistory().ConfigureAwait(true));
        }
    }

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
