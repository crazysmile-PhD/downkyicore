using DownKyi.Application.Desktop;
using DownKyi.Domain.Downloads;
using DownKyi.Services.Download;
using DownKyi.ViewModels.Dialogs;

namespace DownKyi.Tests;

public sealed class DownloadContentConflictDialogViewModelTests
{
    [Fact]
    public void AvailableChoiceReturnsTypedDecisionWithApplyToAll()
    {
        var viewModel = new DownloadContentConflictDialogViewModel
        {
            ApplyToAll = true
        };
        AppDialogResult? result = null;
        viewModel.CloseRequested += (_, value) => result = value;
        viewModel.OnDialogOpened(DownloadContentConflictDialogContract.CreateRequest(CreatePrompt()));

        viewModel.UseAvailableContentCommand.Execute(null);

        Assert.NotNull(result);
        Assert.Equal(AppDialogOutcome.Accepted, result.Outcome);
        var decision = Assert.IsType<DownloadContentConflictDecision>(
            result.Parameters[DownloadContentConflictDialogContract.DecisionParameter]);
        Assert.Equal(DownloadContentConflictAction.UseAvailableMedia, decision.Action);
        Assert.True(decision.ApplyToAll);
    }

    [Fact]
    public void SkipChoiceReturnsTypedSkipDecision()
    {
        var viewModel = new DownloadContentConflictDialogViewModel();
        AppDialogResult? result = null;
        viewModel.CloseRequested += (_, value) => result = value;
        viewModel.OnDialogOpened(DownloadContentConflictDialogContract.CreateRequest(CreatePrompt()));

        viewModel.SkipPageCommand.Execute(null);

        var decision = Assert.IsType<DownloadContentConflictDecision>(
            Assert.IsType<AppDialogResult>(result).Parameters[DownloadContentConflictDialogContract.DecisionParameter]);
        Assert.Equal(DownloadContentConflictAction.SkipPage, decision.Action);
        Assert.False(decision.ApplyToAll);
    }

    [Fact]
    public void DialogRequiresAnExplicitChoice()
    {
        var viewModel = new DownloadContentConflictDialogViewModel();

        Assert.False(viewModel.CanCloseDialog());
    }

    [Fact]
    public void DialogShowsEpisodeQualityAndApiFailure()
    {
        var viewModel = new DownloadContentConflictDialogViewModel();
        var conflict = new DownloadContentConflict(
            DownloadContentSelection.All,
            new DownloadMediaCapabilities(
                DownloadMediaOutputModes.AudioVideo,
                LowerVideoQuality: "720P"),
            DownloadContentSelection.All);

        viewModel.OnDialogOpened(DownloadContentConflictDialogContract.CreateRequest(
            new DownloadContentConflictPrompt(
                "Episode 1", conflict, "BilibiliApiResponseException:-10403")));

        Assert.Contains("720P", viewModel.Message, StringComparison.Ordinal);
        Assert.Contains("-10403", viewModel.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DialogCanOfferOriginallySelectedSidecarsWithoutMedia()
    {
        var viewModel = new DownloadContentConflictDialogViewModel();
        var conflict = new DownloadContentConflict(
            DownloadContentSelection.All,
            new DownloadMediaCapabilities(DownloadMediaOutputModes.None),
            DownloadContentSelection.All with { Audio = false, Video = false });
        AppDialogResult? result = null;
        viewModel.CloseRequested += (_, value) => result = value;

        viewModel.OnDialogOpened(DownloadContentConflictDialogContract.CreateRequest(
            new DownloadContentConflictPrompt("Episode 1", conflict)));
        viewModel.UseAvailableContentCommand.Execute(null);

        var decision = Assert.IsType<DownloadContentConflictDecision>(
            Assert.IsType<AppDialogResult>(result)
                .Parameters[DownloadContentConflictDialogContract.DecisionParameter]);
        Assert.Equal(DownloadContentConflictAction.UseAvailableMedia, decision.Action);
    }

    [Fact]
    public async Task NonAcceptedResultFailsClosed()
    {
        var dialogs = new StubDialogService(new AppDialogResult(
            AppDialogOutcome.Canceled,
            new Dictionary<string, object?>()));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DownloadContentConflictDialogContract.ShowAsync(
                dialogs,
                CreatePrompt(),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AcceptedResultWithoutTypedDecisionFailsClosed()
    {
        var dialogs = new StubDialogService(new AppDialogResult(
            AppDialogOutcome.Accepted,
            new Dictionary<string, object?>()));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DownloadContentConflictDialogContract.ShowAsync(
                dialogs,
                CreatePrompt(),
                TestContext.Current.CancellationToken));
    }

    private static DownloadContentConflictPrompt CreatePrompt() => new(
        "page",
        new DownloadContentConflict(
            DownloadContentSelection.All,
            new DownloadMediaCapabilities(DownloadMediaOutputModes.VideoOnly),
            DownloadContentSelection.All with { Audio = false }));

    private sealed class StubDialogService(AppDialogResult result) : IAppDialogService
    {
        public Task<AppDialogResult> ShowAsync(
            AppDialogRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(result);
    }
}
