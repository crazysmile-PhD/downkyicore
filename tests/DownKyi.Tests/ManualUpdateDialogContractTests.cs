using System.Net;
using DownKyi.Application.Desktop;
using DownKyi.Application.Diagnostics;
using DownKyi.Models;
using DownKyi.Services;
using DownKyi.ViewModels.Dialogs;
using DownKyi.ViewModels.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class ManualUpdateDialogContractTests
{
    [Fact]
    public async Task ManualUpdateSuppliesExplicitNonSkippableDialogContract()
    {
        using var settings = new TestSettingsStore();
        var dialogs = new RecordingDialogService();
        var interactions = new TestDesktopInteractionContext(dialogs: dialogs);
        using var handler = new ReleaseHandler();
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.github.test/")
        };
        using var viewModel = new ViewAboutViewModel(
            interactions,
            settings.Store,
            new StubLogService(),
            new StubPlatformLauncher(),
            new VersionCheckerService(httpClient, "owner", "repo", "1.0.0"),
            NullLogger<ViewAboutViewModel>.Instance);

        viewModel.CheckUpdateCommand.Execute(null);
        var request = await dialogs.Request.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(AppDialog.NewVersionAvailable, request.Dialog);
        var parameters = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
            request.Parameters);
        Assert.IsType<GitHubRelease>(parameters["release"]);
        Assert.False(Assert.IsType<bool>(parameters["enableSkipVersion"]));
    }

    private sealed class RecordingDialogService : IAppDialogService
    {
        public TaskCompletionSource<AppDialogRequest> Request { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<AppDialogResult> ShowAsync(
            AppDialogRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request.TrySetResult(request);
            return Task.FromResult(new AppDialogResult(
                AppDialogOutcome.Canceled,
                new Dictionary<string, object?>()));
        }
    }

    private sealed class ReleaseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"tag_name\":\"v2.0.0\"}")
            });
        }
    }

    private sealed class StubLogService : IApplicationLogService
    {
        public string LogDirectory => string.Empty;

        public IReadOnlyList<ApplicationLogRecord> GetRecentEvents() => [];

        public ApplicationLogMetrics GetMetrics() =>
            new(0, 0, 0, 0, 0, 0, 0, 0, null);

        public Task FlushAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<string> ExportDiagnosticLogAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);
    }

    private sealed class StubPlatformLauncher : IPlatformLauncher
    {
        public Task<bool> OpenFileAsync(
            string path,
            CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<bool> OpenFolderAsync(
            string path,
            CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<bool> OpenUriAsync(
            Uri uri,
            CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
}
