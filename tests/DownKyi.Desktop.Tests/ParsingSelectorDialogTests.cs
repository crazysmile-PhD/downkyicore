using System.Collections.Generic;
using DownKyi.Application.Desktop;
using DownKyi.Core.Settings;
using DownKyi.Services;
using DownKyi.ViewModels.Dialogs;

namespace DownKyi.Desktop.Tests;

public sealed class ParsingSelectorDialogTests
{
    [Theory]
    [InlineData(ParseScope.SelectedItem)]
    [InlineData(ParseScope.CurrentSection)]
    [InlineData(ParseScope.All)]
    public async Task AcceptedExecutableScopeReturnsTypedResultAndIgnoresMetadata(ParseScope scope)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["parseScope"] = scope,
            ["metadata"] = "ignored"
        };
        var service = new DialogServiceStub(new AppDialogResult(
            AppDialogOutcome.Accepted,
            parameters));

        var result = await ParsingSelectorDialog.ShowAsync(
            service,
            TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(scope, result.Scope);
        Assert.Equal(AppDialog.ParsingSelector, service.LastRequest?.Dialog);
    }

    [Theory]
    [InlineData(AppDialogOutcome.Canceled)]
    [InlineData(AppDialogOutcome.Rejected)]
    public async Task NonAcceptedResultEndsNormally(AppDialogOutcome outcome)
    {
        var service = new DialogServiceStub(new AppDialogResult(
            outcome,
            new Dictionary<string, object?>
            {
                ["parseScope"] = "invalid but ignored"
            }));

        var result = await ParsingSelectorDialog.ShowAsync(
            service,
            TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task AcceptedResultWithoutScopeFailsClosed()
    {
        var service = new DialogServiceStub(new AppDialogResult(
            AppDialogOutcome.Accepted,
            new Dictionary<string, object?>
            {
                ["metadata"] = "present"
            }));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ParsingSelectorDialog.ShowAsync(
                service,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AcceptedResultWithWrongScopeTypeFailsClosed()
    {
        var service = new DialogServiceStub(new AppDialogResult(
            AppDialogOutcome.Accepted,
            new Dictionary<string, object?>
            {
                ["parseScope"] = "All"
            }));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ParsingSelectorDialog.ShowAsync(
                service,
                TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(ParseScope.NotSet)]
    [InlineData(ParseScope.None)]
    [InlineData((ParseScope)999)]
    public async Task AcceptedNonExecutableScopeFailsClosed(ParseScope scope)
    {
        var service = new DialogServiceStub(new AppDialogResult(
            AppDialogOutcome.Accepted,
            new Dictionary<string, object?>
            {
                ["parseScope"] = scope
            }));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ParsingSelectorDialog.ShowAsync(
                service,
                TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(ParseScope.NotSet)]
    [InlineData(ParseScope.None)]
    [InlineData((ParseScope)999)]
    public void EncoderRejectsNonExecutableScope(ParseScope scope)
    {
        Assert.Throws<InvalidOperationException>(() =>
            ParsingSelectorDialog.Encode(new ParsingSelectorResult(scope)));
    }

    [AvaloniaFact]
    public async Task SelectorCommandsProduceTypedResultsAndPreserveDefaultSettingBehavior()
    {
        DesktopTestResources.EnsureProductThemeResources();
        foreach (var scope in new[]
        {
            ParseScope.SelectedItem,
            ParseScope.CurrentSection,
            ParseScope.All
        })
        {
            var settingsPath = Path.Combine(
                Path.GetTempPath(),
                $"downkyi-parsing-selector-{Guid.NewGuid():N}.json");
            using var settings = new SettingsStore(settingsPath);
            var viewModel = new ViewParsingSelectorViewModel(settings)
            {
                IsParseDefault = true
            };
            AppDialogResult? rawResult = null;
            viewModel.CloseRequested += (_, result) => rawResult = result;

            ExecuteSelection(viewModel, scope);

            Assert.NotNull(rawResult);
            var typedResult = await ParsingSelectorDialog.ShowAsync(
                new DialogServiceStub(rawResult),
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.NotNull(typedResult);
            Assert.Equal(scope, typedResult.Scope);
            Assert.Equal(scope, settings.Current.Basic.ParseScope);
        }
    }

    private static void ExecuteSelection(ViewParsingSelectorViewModel viewModel, ParseScope scope)
    {
        switch (scope)
        {
            case ParseScope.SelectedItem:
                viewModel.ParseSelectedItemCommand.Execute(null);
                break;
            case ParseScope.CurrentSection:
                viewModel.ParseCurrentSectionCommand.Execute(null);
                break;
            case ParseScope.All:
                viewModel.ParseAllCommand.Execute(null);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scope), scope, null);
        }
    }

    private sealed class DialogServiceStub(AppDialogResult result) : IAppDialogService
    {
        public AppDialogRequest? LastRequest { get; private set; }

        public Task<AppDialogResult> ShowAsync(
            AppDialogRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            return Task.FromResult(result);
        }
    }
}
