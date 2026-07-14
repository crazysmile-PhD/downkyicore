using System;
using System.Collections.Generic;
using System.Linq;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.Settings;
using DownKyi.ViewModels.PageViewModels;

namespace DownKyi.Services.Video;

internal static class VideoSelectionState
{
    public static VideoSection? GetSelectedSection(IEnumerable<VideoSection> sections)
    {
        return sections.FirstOrDefault(section => section.IsSelected);
    }

    public static List<VideoPage> GetSelectedPages(VideoSection? section)
    {
        return section?.VideoPages.Where(page => page.IsSelected).ToList() ?? new List<VideoPage>();
    }

    public static void ApplySelectedPages(VideoSection section, IEnumerable<VideoPage> selectedPages)
    {
        var selectedCids = new HashSet<long>(selectedPages.Select(page => page.Cid));
        foreach (var videoPage in section.VideoPages)
        {
            videoPage.IsSelected = selectedCids.Contains(videoPage.Cid);
        }
    }

    public static bool IsAllSelected(VideoSection? section, int selectedCount)
    {
        return section?.VideoPages.Count > 0 && selectedCount == section.VideoPages.Count;
    }

    public static void SetAllSelected(VideoSection? section, bool isSelected)
    {
        if (section == null)
        {
            return;
        }

        foreach (var page in section.VideoPages)
        {
            page.IsSelected = isSelected;
        }
    }

    public static VideoPage? SelectInputPage(IEnumerable<VideoSection> sections, string input)
    {
        ArgumentNullException.ThrowIfNull(sections);
        var avid = ParseEntrance.GetAvId(input);
        var bvid = ParseEntrance.GetBvId(input);
        foreach (var section in sections)
        {
            section.IsSelected = true;
            var page = section.VideoPages.FirstOrDefault(item => item.Avid == avid || item.Bvid == bvid);
            if (page == null)
            {
                continue;
            }

            page.IsSelected = true;
            return page;
        }

        return null;
    }

    public static void ApplyVisibleSelectionDelta(
        IReadOnlySet<VideoPage> visiblePages,
        IEnumerable<VideoPage> removedPages,
        IEnumerable<VideoPage> addedPages)
    {
        ArgumentNullException.ThrowIfNull(visiblePages);
        ArgumentNullException.ThrowIfNull(removedPages);
        ArgumentNullException.ThrowIfNull(addedPages);

        foreach (var page in removedPages.Where(visiblePages.Contains))
        {
            page.IsSelected = false;
        }

        foreach (var page in addedPages.Where(visiblePages.Contains))
        {
            page.IsSelected = true;
        }
    }

    public static List<VideoPage> GetPagesForScope(IEnumerable<VideoSection> sections, ParseScope parseScope)
    {
        return parseScope switch
        {
            ParseScope.SelectedItem => sections
                .SelectMany(section => section.VideoPages)
                .Where(page => page.IsSelected)
                .ToList(),
            ParseScope.CurrentSection => sections
                .Where(section => section.IsSelected)
                .SelectMany(section => section.VideoPages)
                .ToList(),
            ParseScope.All => sections
                .SelectMany(section => section.VideoPages)
                .ToList(),
            _ => new List<VideoPage>()
        };
    }
}
