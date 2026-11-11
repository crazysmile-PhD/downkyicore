using DownKyi.Application.Desktop;
using DownKyi.Services.Download;
using DownKyi.ViewModels.Dialogs;

namespace DownKyi.Tests;

public sealed class DownloadUnavailableBatchDialogViewModelTests
{
    [Fact]
    public void PromptShowsEveryFailureWithReasonAndOptionalApiCode()
    {
        var viewModel = new DownloadUnavailableBatchDialogViewModel();

        viewModel.OnDialogOpened(DownloadUnavailableBatchDialogContract.CreateRequest(
            new DownloadUnavailableBatchPrompt(
            [
                new DownloadUnavailableBatchPromptItem(
                    "没有试看内容的充电影片",
                    "BV1ADgC6sEUj",
                    DownloadUnavailableReason.AccessRestrictedOrUnavailable,
                    -404),
                new DownloadUnavailableBatchPromptItem(
                    "接口返回了无法识别的结果",
                    "BV17x411w7KC",
                    DownloadUnavailableReason.Unknown)
            ])));

        Assert.Equal(2, viewModel.Items.Count);
        Assert.Equal("没有试看内容的充电影片", viewModel.Items[0].DisplayName);
        Assert.Equal("BV1ADgC6sEUj", viewModel.Items[0].Source);
        Assert.Equal("接口返回了无法识别的结果", viewModel.Items[1].DisplayName);
        Assert.Empty(viewModel.Items[1].TechnicalDetail);
        Assert.False(viewModel.CanCloseDialog());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExplicitChoiceReturnsTypedDecision(bool skipUnavailable)
    {
        var viewModel = new DownloadUnavailableBatchDialogViewModel();
        AppDialogResult? result = null;
        viewModel.CloseRequested += (_, value) => result = value;
        viewModel.OnDialogOpened(DownloadUnavailableBatchDialogContract.CreateRequest(
            new DownloadUnavailableBatchPrompt(
            [
                new DownloadUnavailableBatchPromptItem(
                    "无法下载的影片",
                    "BV1ADgC6sEUj",
                    DownloadUnavailableReason.Unknown)
            ])));

        if (skipUnavailable)
        {
            viewModel.SkipCommand.Execute(null);
        }
        else
        {
            viewModel.CancelCommand.Execute(null);
        }

        Assert.Equal(
            skipUnavailable
                ? DownloadUnavailableBatchAction.SkipUnavailable
                : DownloadUnavailableBatchAction.CancelBatch,
            Assert.IsType<DownloadUnavailableBatchAction>(
                Assert.IsType<AppDialogResult>(result)
                    .Parameters[DownloadUnavailableBatchDialogContract.DecisionParameter]));
    }

    [Fact]
    public void UnsupportedReasonDoesNotSilentlyFallBackToUnknown()
    {
        var viewModel = new DownloadUnavailableBatchDialogViewModel();
        var request = DownloadUnavailableBatchDialogContract.CreateRequest(
            new DownloadUnavailableBatchPrompt(
            [
                new DownloadUnavailableBatchPromptItem(
                    "无法下载的影片",
                    "BV1ADgC6sEUj",
                    (DownloadUnavailableReason)int.MaxValue)
            ]));

        Assert.Throws<ArgumentOutOfRangeException>(() => viewModel.OnDialogOpened(request));
    }
}
