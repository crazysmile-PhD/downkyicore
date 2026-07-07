using Avalonia.Media;
using Prism.Mvvm;

namespace DownKyi.ViewModels.UserSpace;

public class PublicationZone : BindableBase
{
    public int Tid { get; set; }

    private DrawingImage icon = null!;

    public DrawingImage Icon
    {
        get => icon;
        set => SetProperty(ref icon, value);
    }

    private string name = string.Empty;

    public string Name
    {
        get => name;
        set => SetProperty(ref name, value);
    }

    private int count;

    public int Count
    {
        get => count;
        set => SetProperty(ref count, value);
    }
}
