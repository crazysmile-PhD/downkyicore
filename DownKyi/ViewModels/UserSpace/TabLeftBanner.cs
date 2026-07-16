using DownKyi.Images;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DownKyi.ViewModels.UserSpace;

internal class TabLeftBanner : ObservableObject
{
    public object NavigationData { get; set; } = new();

    public int Id { get; set; }

    private bool isSelected;

    public bool IsSelected
    {
        get => isSelected;
        set => SetProperty(ref isSelected, value);
    }

    private VectorImage icon = null!;

    public VectorImage Icon
    {
        get => icon;
        set => SetProperty(ref icon, value);
    }

    private string iconColor = string.Empty;

    public string IconColor
    {
        get => iconColor;
        set => SetProperty(ref iconColor, value);
    }

    private string title = string.Empty;

    public string Title
    {
        get => title;
        set => SetProperty(ref title, value);
    }
}
