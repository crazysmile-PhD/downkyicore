using Prism.Mvvm;

namespace DownKyi.ViewModels.UserSpace;

public class TabRightBanner : BindableBase
{
    public int Id { get; set; }

    private bool isEnabled;

    public bool IsEnabled
    {
        get => isEnabled;
        set => SetProperty(ref isEnabled, value);
    }

    private string labelColor = string.Empty;

    public string LabelColor
    {
        get => labelColor;
        set => SetProperty(ref labelColor, value);
    }

    private string countColor = string.Empty;

    public string CountColor
    {
        get => countColor;
        set => SetProperty(ref countColor, value);
    }

    private string label = string.Empty;

    public string Label
    {
        get => label;
        set => SetProperty(ref label, value);
    }

    private string count = string.Empty;

    public string Count
    {
        get => count;
        set => SetProperty(ref count, value);
    }
}
