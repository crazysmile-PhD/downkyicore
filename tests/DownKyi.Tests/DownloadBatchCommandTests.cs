using DownKyi.Application.Desktop;
using DownKyi.Services.Download;
using DownKyi.ViewModels.DownloadManager;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class DownloadBatchCommandTests
{
    [Fact]
    public void PauseResumePauseClicksAreAllForwardedWhileEarlierBatchesRun()
    {
        var coordinator = new BlockingCoordinator();
        using var viewModel = new ViewDownloadingViewModel(
            new TestDesktopInteractionContext(),
            new DownloadListState(),
            coordinator,
            NullLogger<ViewDownloadingViewModel>.Instance);

        viewModel.PauseAllDownloadingCommand.Execute(null);
        viewModel.ContinueAllDownloadingCommand.Execute(null);
        viewModel.PauseAllDownloadingCommand.Execute(null);

        Assert.Equal(["pause", "resume", "pause"], coordinator.Operations);
        coordinator.AllowCompletion.TrySetResult();
    }

    private sealed class BlockingCoordinator : IDownloadManagerCoordinator
    {
        public List<string> Operations { get; } = [];

        public TaskCompletionSource AllowCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task PauseAllAsync(
            IEnumerable<DownloadingItem> items,
            CancellationToken cancellationToken = default)
        {
            Operations.Add("pause");
            return AllowCompletion.Task;
        }

        public Task ResumeAllAsync(
            IEnumerable<DownloadingItem> items,
            CancellationToken cancellationToken = default)
        {
            Operations.Add("resume");
            return AllowCompletion.Task;
        }

        public Task ToggleAsync(
            DownloadingItem item,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteAsync(
            DownloadingItem item,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteAllAsync(
            IEnumerable<DownloadingItem> items,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ClearDownloadedAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

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
}
