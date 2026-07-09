using System.Collections.Generic;
using System.Linq;
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
