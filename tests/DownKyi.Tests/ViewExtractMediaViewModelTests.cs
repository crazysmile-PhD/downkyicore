using DownKyi.ViewModels.Toolbox;
using Prism.Events;

namespace DownKyi.Tests;

public sealed class ViewExtractMediaViewModelTests
{
    [Fact]
    public void VideoPaths_PreservesBindingNameAndDisplayText()
    {
        using var viewModel = new ViewExtractMediaViewModel(new EventAggregator())
        {
            VideoPaths = new[] { "first.mp4", "second.mp4" }
        };

        Assert.Equal(2, viewModel.VideoPaths.Count);
        Assert.Equal($"first.mp4{Environment.NewLine}second.mp4", viewModel.VideoPathsStr);
    }
}
