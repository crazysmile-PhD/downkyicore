using DownKyi.Services.Video;
using DownKyi.ViewModels.PageViewModels;

namespace DownKyi.Tests;

public sealed class VideoSearchStateTests
{
    [Fact]
    public void ApplyFiltersFromOneSourceWithoutCloningPages()
    {
        var alpha = new VideoPage { Cid = 1, Name = "Alpha" };
        var beta = new VideoPage { Cid = 2, Name = "Beta" };
        var section = new VideoSection
        {
            Id = 10,
            IsSelected = true,
            VideoPages = new List<VideoPage> { alpha, beta }
        };
        var state = new VideoSearchState();
        state.Reset([section]);

        state.Apply("Beta");

        var visiblePage = Assert.Single(section.VideoPages);
        Assert.Same(beta, visiblePage);
    }

    [Fact]
    public void ClearingSearchPreservesSelectionAndParsedStateChanges()
    {
        var alpha = new VideoPage { Cid = 1, Name = "Alpha" };
        var beta = new VideoPage { Cid = 2, Name = "Beta" };
        var section = new VideoSection
        {
            Id = 10,
            IsSelected = true,
            VideoPages = new List<VideoPage> { alpha, beta }
        };
        var state = new VideoSearchState();
        state.Reset([section]);
        state.Apply("Beta");

        beta.IsSelected = true;
        beta.Duration = "03:00";
        state.Apply(string.Empty);

        Assert.Equal(2, section.VideoPages.Count);
        Assert.Same(beta, section.VideoPages[1]);
        Assert.True(section.VideoPages[1].IsSelected);
        Assert.Equal("03:00", section.VideoPages[1].Duration);
    }
}
