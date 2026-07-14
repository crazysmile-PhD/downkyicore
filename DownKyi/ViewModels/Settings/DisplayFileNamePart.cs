using DownKyi.Core.FileName;
using Prism.Mvvm;

namespace DownKyi.ViewModels.Settings;

internal class DisplayFileNamePart : BindableBase
{
    public FileNamePart Id { get; set; }

    private string _title = string.Empty;

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }
}
