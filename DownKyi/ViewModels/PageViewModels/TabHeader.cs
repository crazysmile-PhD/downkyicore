using DownKyi.Images;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DownKyi.ViewModels.PageViewModels;

internal class TabHeader : ObservableObject
{
    private long id;

    public long Id
    {
        get => id;
        set => SetProperty(ref id, value);
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

    private string subTitle = string.Empty;

    public string SubTitle
    {
        get => subTitle;
        set => SetProperty(ref subTitle, value);
    }
}
