using CommunityToolkit.Mvvm.ComponentModel;
using DownKyi.Core.FileName;

namespace DownKyi.ViewModels.Settings;

internal class DisplayFileNamePart : ObservableObject
{
    public FileNamePart Id { get; set; }

    private string _title = string.Empty;

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }
}
