using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DownKyi.ViewModels.UserSpace;

internal class PublicationZone : ObservableObject
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
