using System;
using System.Collections.Generic;
using System.Linq;
using DownKyi.ViewModels.PageViewModels;

namespace DownKyi.Services.Video;

internal sealed class VideoSearchState
{
    private readonly List<SectionSource> _sources = [];

    public void Reset(IEnumerable<VideoSection> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);

        _sources.Clear();
        foreach (var section in sections)
        {
            _sources.Add(new SectionSource(section, new List<VideoPage>(section.VideoPages)));
        }
    }

    public void Clear()
    {
        _sources.Clear();
    }

    public void Apply(string? searchText)
    {
        foreach (var source in _sources)
        {
            source.Section.VideoPages = string.IsNullOrEmpty(searchText)
                ? source.Pages
                : source.Pages
                    .Where(page => page.Name.Contains(searchText, StringComparison.Ordinal))
                    .ToList();
        }
    }

    private sealed record SectionSource(VideoSection Section, IList<VideoPage> Pages);
}
