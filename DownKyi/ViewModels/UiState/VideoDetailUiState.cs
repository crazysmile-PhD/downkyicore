using CommunityToolkit.Mvvm.ComponentModel;
using DownKyi.Images;
using DownKyi.ViewModels.PageViewModels;

namespace DownKyi.ViewModels.UiState;

internal enum VideoDetailDisplayState
{
    Idle,
    Busy,
    Content,
    Empty
}

internal sealed partial class VideoDetailUiState : ObservableObject
{
    [ObservableProperty]
    private string? _inputText;

    [ObservableProperty]
    private string _inputSearchText = string.Empty;

    [ObservableProperty]
    private VectorImage _downloadManage = ButtonIcon.Instance().DownloadManage;

    [ObservableProperty]
    private VideoInfoView? _videoInfoView;

    [ObservableProperty]
    private bool _isSelectAll;

    [ObservableProperty]
    private int _gridResetVersion;

    [ObservableProperty]
    private VideoPage? _selectedVideoPage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(IsContentVisible))]
    [NotifyPropertyChangedFor(nameof(IsEmptyVisible))]
    private VideoDetailDisplayState _displayState;

    public bool IsBusy => DisplayState == VideoDetailDisplayState.Busy;

    public bool IsContentVisible => DisplayState == VideoDetailDisplayState.Content;

    public bool IsEmptyVisible => DisplayState == VideoDetailDisplayState.Empty;
}
