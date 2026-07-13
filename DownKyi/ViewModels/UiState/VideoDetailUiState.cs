using CommunityToolkit.Mvvm.ComponentModel;

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
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(IsContentVisible))]
    [NotifyPropertyChangedFor(nameof(IsEmptyVisible))]
    private VideoDetailDisplayState _displayState;

    public bool IsBusy => DisplayState == VideoDetailDisplayState.Busy;

    public bool IsContentVisible => DisplayState == VideoDetailDisplayState.Content;

    public bool IsEmptyVisible => DisplayState == VideoDetailDisplayState.Empty;
}
