using DownKyi.ViewModels.UiState;

namespace DownKyi.Tests;

public sealed class VideoDetailUiStateTests
{
    [Theory]
    [InlineData((int)VideoDetailDisplayState.Idle, false, false, false)]
    [InlineData((int)VideoDetailDisplayState.Busy, true, false, false)]
    [InlineData((int)VideoDetailDisplayState.Content, false, true, false)]
    [InlineData((int)VideoDetailDisplayState.Empty, false, false, true)]
    public void DisplayStateKeepsDerivedVisibilityMutuallyExclusive(
        int stateValue,
        bool expectedBusy,
        bool expectedContent,
        bool expectedEmpty)
    {
        var uiState = new VideoDetailUiState
        {
            DisplayState = (VideoDetailDisplayState)stateValue
        };

        Assert.Equal(expectedBusy, uiState.IsBusy);
        Assert.Equal(expectedContent, uiState.IsContentVisible);
        Assert.Equal(expectedEmpty, uiState.IsEmptyVisible);
        Assert.InRange(
            new[] { uiState.IsBusy, uiState.IsContentVisible, uiState.IsEmptyVisible }.Count(value => value),
            0,
            1);
    }
}
