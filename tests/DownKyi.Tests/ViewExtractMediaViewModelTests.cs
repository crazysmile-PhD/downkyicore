using DownKyi.Application.Desktop;
using DownKyi.Core.FFMpeg;
using DownKyi.ViewModels.Toolbox;
using Microsoft.Extensions.Logging.Abstractions;
using Prism.Events;

namespace DownKyi.Tests;

public sealed class ViewExtractMediaViewModelTests
{
    [Fact]
    public void VideoPathsPreservesBindingNameAndDisplayText()
    {
        using var settings = new TestSettingsStore();
        using var viewModel = new ViewExtractMediaViewModel(
            new EventAggregator(),
            new StubFilePickerService(),
            new FfmpegProcessor(settings.Store, NullLoggerFactory.Instance),
            NullLogger<ViewExtractMediaViewModel>.Instance)
        {
            VideoPaths = new[] { "first.mp4", "second.mp4" }
        };

        Assert.Equal(2, viewModel.VideoPaths.Count);
        Assert.Equal($"first.mp4{Environment.NewLine}second.mp4", viewModel.VideoPathsStr);
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
