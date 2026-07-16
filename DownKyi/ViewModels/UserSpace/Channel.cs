using CommunityToolkit.Mvvm.ComponentModel;

namespace DownKyi.ViewModels.UserSpace;

internal class Channel : ObservableObject
{
    public long Cid { get; set; }

    /*private ImageSource cover;
    public ImageSource Cover
    {
        get => cover;
        set => SetProperty(ref cover, value);
    }*/

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

    private string ctime = string.Empty;

    public string Ctime
    {
        get => ctime;
        set => SetProperty(ref ctime, value);
    }
}
