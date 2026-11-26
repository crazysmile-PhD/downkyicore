using System.Windows.Input;
using DownKyi.Images;

namespace DownKyi.ViewModels;

internal interface IIndexSurfaceViewModel
{
    string? UserName { get; }
    string Header { get; }
    VectorImage TextLogo { get; }
    VectorImage GeneralSearch { get; }
    VectorImage Settings { get; }
    VectorImage DownloadManager { get; }
    VectorImage Toolbox { get; }
    string InputText { get; set; }
    ICommand InputCommand { get; }
    ICommand LoginCommand { get; }
    ICommand SettingsCommand { get; }
    ICommand DownloadManagerCommand { get; }
    ICommand ToolboxCommand { get; }
}
