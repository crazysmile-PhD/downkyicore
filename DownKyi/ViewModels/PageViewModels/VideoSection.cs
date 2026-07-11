using System.Collections.Generic;
using System.Linq;
using Prism.Mvvm;

namespace DownKyi.ViewModels.PageViewModels;

public class VideoSection : BindableBase
{
    public long Id { get; set; }

    private string _title = string.Empty;

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    private bool _isSelected;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    private IList<VideoPage> _videoPages = new List<VideoPage>();

    public IList<VideoPage> VideoPages
    {
        get => _videoPages;
        internal set => SetProperty(ref _videoPages, value);
    }

    public VideoSection CloneForCache()
    {
        return new VideoSection
        {
            Id = Id,
            Title = Title,
            IsSelected = IsSelected,
            VideoPages = VideoPages.Select(page => page.CloneForCache()).ToList()
        };
    }
}
