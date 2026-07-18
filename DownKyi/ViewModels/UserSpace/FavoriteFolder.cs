using DownKyi.Images;
using Prism.Mvvm;

namespace DownKyi.ViewModels.UserSpace;

internal class FavoriteFolder : BindableBase
{
    public long Id { get; set; }

    private string _cover = string.Empty;

    public string Cover
    {
        get => _cover;
        set => SetProperty(ref _cover, value);
    }

    private VectorImage _typeImage = null!;

    public VectorImage TypeImage
    {
        get => _typeImage;
        set => SetProperty(ref _typeImage, value);
    }

    private string _title = string.Empty;

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    private int _count;

    public int Count
    {
        get => _count;
        set => SetProperty(ref _count, value);
    }

    private string _updatedAt = string.Empty;

    public string UpdatedAt
    {
        get => _updatedAt;
        set => SetProperty(ref _updatedAt, value);
    }
}
