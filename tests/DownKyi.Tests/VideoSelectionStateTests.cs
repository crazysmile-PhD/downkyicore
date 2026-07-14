using System.ComponentModel;
using DownKyi.Core.Settings;
using DownKyi.CustomControl;
using DownKyi.Services.Video;
using DownKyi.ViewModels.PageViewModels;

namespace DownKyi.Tests;

public sealed class VideoSelectionStateTests
{
    [Fact]
    public void PagerEventsPreserveVetoAndCountSemantics()
    {
        var pager = new CustomPagerViewModel(1, 3);
        CancelEventArgs? changing = null;
        var countChanged = false;

        pager.CurrentChanging += (_, e) =>
        {
            changing = e;
            e.Cancel = true;
        };
        pager.CountChanged += (_, _) => countChanged = true;

        pager.Current = 2;
        pager.Count = 4;

        Assert.NotNull(changing);
        Assert.True(changing.Cancel);
        Assert.Equal(2, pager.ProposedCurrent);
        Assert.Equal(1, pager.Current);
        Assert.True(countChanged);
    }

    [Fact]
    public void GetPagesForScopeReturnsOnlySelectedPagesForSelectedItemScope()
    {
        var sections = CreateSections();

        var pages = VideoSelectionState.GetPagesForScope(sections, ParseScope.SelectedItem);

        Assert.Equal(new long[] { 101, 202 }, pages.Select(page => page.Cid));
    }

    [Fact]
    public void GetPagesForScopeReturnsSelectedSectionPagesForCurrentSectionScope()
    {
        var sections = CreateSections();

        var pages = VideoSelectionState.GetPagesForScope(sections, ParseScope.CurrentSection);

        Assert.Equal(new long[] { 101, 102 }, pages.Select(page => page.Cid));
    }

    [Fact]
    public void GetPagesForScopeReturnsAllPagesForAllScope()
    {
        var sections = CreateSections();

        var pages = VideoSelectionState.GetPagesForScope(sections, ParseScope.All);

        Assert.Equal(new long[] { 101, 102, 201, 202 }, pages.Select(page => page.Cid));
    }

    [Fact]
    public void ApplySelectedPagesUpdatesSectionSelectionByCid()
    {
        var section = CreateSections()[0];

        VideoSelectionState.ApplySelectedPages(section, new[] { section.VideoPages[1] });

        Assert.False(section.VideoPages[0].IsSelected);
        Assert.True(section.VideoPages[1].IsSelected);
        Assert.False(VideoSelectionState.IsAllSelected(section, VideoSelectionState.GetSelectedPages(section).Count));

        VideoSelectionState.ApplySelectedPages(section, section.VideoPages);

        Assert.True(VideoSelectionState.IsAllSelected(section, VideoSelectionState.GetSelectedPages(section).Count));
    }

    [Fact]
    public void SetAllSelectedSupportsSelectAllAndClearSelection()
    {
        var section = CreateSections()[0];

        VideoSelectionState.SetAllSelected(section, isSelected: true);
        Assert.All(section.VideoPages, page => Assert.True(page.IsSelected));

        VideoSelectionState.SetAllSelected(section, isSelected: false);
        Assert.All(section.VideoPages, page => Assert.False(page.IsSelected));
    }

    [Fact]
    public void SelectInputPageMarksMatchingSectionAndOriginalPage()
    {
        var page = new VideoPage { Cid = 42, Bvid = "BV17x411w7KC" };
        var section = new VideoSection { Id = 1, VideoPages = [page] };

        var selected = VideoSelectionState.SelectInputPage([section], "BV17x411w7KC");

        Assert.Same(page, selected);
        Assert.True(section.IsSelected);
        Assert.True(page.IsSelected);
    }

    [Fact]
    public void ApplyVisibleSelectionDeltaPreservesSelectionsFromAnotherSection()
    {
        var oldSectionPage = new VideoPage { Cid = 101, IsSelected = true };
        var visiblePage = new VideoPage { Cid = 201, IsSelected = false };
        IReadOnlySet<VideoPage> visiblePages = new HashSet<VideoPage> { visiblePage };

        VideoSelectionState.ApplyVisibleSelectionDelta(
            visiblePages,
            [oldSectionPage],
            [visiblePage]);

        Assert.True(oldSectionPage.IsSelected);
        Assert.True(visiblePage.IsSelected);

        VideoSelectionState.ApplyVisibleSelectionDelta(
            visiblePages,
            [visiblePage],
            []);

        Assert.False(visiblePage.IsSelected);
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
