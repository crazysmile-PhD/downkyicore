using System;
using System.Collections.Generic;
using System.Linq;
using DownKyi.Application.Media;
using DownKyi.Presentation;

namespace DownKyi.Services.Video;

internal static class VideoSelectionState
{
    public static VideoSection? GetSelectedSection(IEnumerable<VideoSection> sections)
    {
        return sections.FirstOrDefault(section => section.IsSelected);
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

    public static VideoPage? SelectInputPage(
        IEnumerable<VideoSection> sections,
        BilibiliMediaInput input)
    {
        ArgumentNullException.ThrowIfNull(sections);
        foreach (var section in sections)
        {
            section.IsSelected = true;
            var page = input switch
            {
                BilibiliAvidInput avid => section.VideoPages.FirstOrDefault(
                    item => item.Avid == avid.Avid),
                BilibiliBvidInput bvid => section.VideoPages.FirstOrDefault(
                    item => string.Equals(item.Bvid, bvid.Bvid, StringComparison.Ordinal)),
                _ => null
            };
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

}
