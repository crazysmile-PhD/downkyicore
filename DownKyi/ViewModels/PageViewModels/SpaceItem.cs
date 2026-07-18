using CommunityToolkit.Mvvm.ComponentModel;
using DownKyi.Images;

namespace DownKyi.ViewModels.PageViewModels;

internal class SpaceItem : ObservableObject
{
    private bool _isEnabled;

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    private VectorImage image = null!;

    public VectorImage Image
    {
        get => image;
        set => SetProperty(ref image, value);
    }

    private string title = string.Empty;

    public string Title
    {
        get => title;
        set => SetProperty(ref title, value);
    }

    private string subtitle = string.Empty;

    public string Subtitle
    {
        get => subtitle;
        set => SetProperty(ref subtitle, value);
    }
}
