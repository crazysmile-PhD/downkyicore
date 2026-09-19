using System.Collections.Specialized;
using DownKyi.Application.Desktop;
using DownKyi.ViewModels.Dialogs;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class ViewDownloadSetterSelectionTests
{
    [Fact]
    public void DownloadCommandPreservesSelectionWhenReorderingReentersTheBinding()
    {
        using var settings = new TestSettingsStore();
        var interaction = new TestDesktopInteractionContext();
        var viewModel = new ViewDownloadSetterViewModel(
            interaction.Notifications,
            new StubFilePickerService(),
            settings.Store,
            NullLogger<ViewDownloadSetterViewModel>.Instance);
        var selectedDirectory = Path.GetTempPath();
        var reentrantSelection = Path.GetPathRoot(selectedDirectory)!;
        viewModel.DirectoryList.Clear();
        viewModel.DirectoryList.Add("other-directory");
        viewModel.DirectoryList.Add(selectedDirectory);
        viewModel.Directory = selectedDirectory;
        var observedSelections = new List<string>();
        viewModel.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(viewModel.Directory))
            {
                observedSelections.Add(viewModel.Directory);
            }
        };
        viewModel.DirectoryList.CollectionChanged += (_, eventArgs) =>
        {
            if (eventArgs.Action == NotifyCollectionChangedAction.Remove)
            {
                viewModel.Directory = reentrantSelection;
            }
        };

        viewModel.DownloadCommand.Execute(null);

        Assert.Equal(selectedDirectory, viewModel.Directory);
        Assert.Equal(selectedDirectory, viewModel.DirectoryList[0]);
        Assert.Equal(selectedDirectory, settings.Store.Current.Video.SaveVideoRootPath);
        Assert.Equal([reentrantSelection, selectedDirectory], observedSelections);
    }

    private sealed class StubFilePickerService : IFilePickerService
    {
        public Task<string?> SelectFolderAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<string?> SelectVideoAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> SelectVideosAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
