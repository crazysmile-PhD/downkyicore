using DownKyi.Core.Settings;
using DownKyi.Services.Video;
using DownKyi.ViewModels.PageViewModels;

namespace DownKyi.Tests;

public sealed class VideoSelectionStateTests
{
    [Fact]
    public void GetPagesForScope_ReturnsOnlySelectedPages_ForSelectedItemScope()
    {
        var sections = CreateSections();

        var pages = VideoSelectionState.GetPagesForScope(sections, ParseScope.SelectedItem);

        Assert.Equal(new long[] { 101, 202 }, pages.Select(page => page.Cid));
    }

    [Fact]
    public void GetPagesForScope_ReturnsSelectedSectionPages_ForCurrentSectionScope()
    {
        var sections = CreateSections();

        var pages = VideoSelectionState.GetPagesForScope(sections, ParseScope.CurrentSection);

        Assert.Equal(new long[] { 101, 102 }, pages.Select(page => page.Cid));
    }

    [Fact]
    public void GetPagesForScope_ReturnsAllPages_ForAllScope()
    {
        var sections = CreateSections();

        var pages = VideoSelectionState.GetPagesForScope(sections, ParseScope.All);

        Assert.Equal(new long[] { 101, 102, 201, 202 }, pages.Select(page => page.Cid));
    }

    [Fact]
    public void ApplySelectedPages_UpdatesSectionSelectionByCid()
    {
        var section = CreateSections()[0];

        VideoSelectionState.ApplySelectedPages(section, new[] { section.VideoPages[1] });

        Assert.False(section.VideoPages[0].IsSelected);
        Assert.True(section.VideoPages[1].IsSelected);
        Assert.False(VideoSelectionState.IsAllSelected(section, VideoSelectionState.GetSelectedPages(section).Count));

        VideoSelectionState.ApplySelectedPages(section, section.VideoPages);

        Assert.True(VideoSelectionState.IsAllSelected(section, VideoSelectionState.GetSelectedPages(section).Count));
    }

    private static List<VideoSection> CreateSections()
    {
        return new List<VideoSection>
        {
            new()
            {
                Id = 1,
                IsSelected = true,
                VideoPages = new List<VideoPage>
                {
                    new() { Cid = 101, IsSelected = true },
                    new() { Cid = 102, IsSelected = false }
                }
            },
            new()
            {
                Id = 2,
                IsSelected = false,
                VideoPages = new List<VideoPage>
                {
                    new() { Cid = 201, IsSelected = false },
                    new() { Cid = 202, IsSelected = true }
                }
            }
        };
    }
}
