using System.Collections.Immutable;
using System.Collections.Specialized;
using DownKyi.Application.Desktop;
using DownKyi.ViewModels.Dialogs;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class ViewDownloadSetterViewModelTests
{
    [Fact]
    public void DownloadCommandRestoresSelectedDirectoryAfterMovingItToFront()
    {
        using var settings = new TestSettingsStore();
        var firstDirectory = Path.Combine(Path.GetTempPath(), "downkyi-first-directory");
        var selectedDirectory = Path.Combine(Path.GetTempPath(), "downkyi-selected-directory");
        settings.Store.Update(current => current with
        {
            Video = current.Video with
            {
                SaveVideoRootPath = selectedDirectory,
                HistoryVideoRootPaths = ImmutableArray.Create(firstDirectory, selectedDirectory)
            }
        });
        var interactions = new TestDesktopInteractionContext();
        var viewModel = new ViewDownloadSetterViewModel(
            interactions.Notifications,
            new StubFilePickerService(),
            settings.Store,
            NullLogger<ViewDownloadSetterViewModel>.Instance);

        viewModel.DirectoryList.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Remove &&
                args.OldItems?.Contains(selectedDirectory) == true)
            {
                // Avalonia selection binding can synchronously clear the selected value
                // when its item is removed before being reinserted.
                viewModel.Directory = string.Empty;
            }
        };

        viewModel.DownloadCommand.Execute(null);

        Assert.Equal(selectedDirectory, viewModel.Directory);
        Assert.Equal(selectedDirectory, viewModel.DirectoryList[0]);
        Assert.Single(viewModel.DirectoryList, path =>
            string.Equals(path, selectedDirectory, StringComparison.Ordinal));
        Assert.Equal(selectedDirectory, settings.Store.Current.Video.SaveVideoRootPath);
        Assert.Equal(selectedDirectory, settings.Store.Current.Video.HistoryVideoRootPaths[0]);
    }

    private sealed class StubFilePickerService : IFilePickerService
    {
        public Task<string?> SelectFolderAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<string?> SelectVideoAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> SelectVideosAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }
}
