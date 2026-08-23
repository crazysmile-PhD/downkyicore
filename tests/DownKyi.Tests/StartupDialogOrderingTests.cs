using System.Net;
using DownKyi.Application.Desktop;
using DownKyi.Core.Settings;
using DownKyi.Platform;
using DownKyi.Services;
using DownKyi.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class StartupDialogOrderingTests
{
    [Fact]
    public async Task UpdateDialogWaitsForLegacyMigrationDialogToFinish()
    {
        using var settings = new TestSettingsStore();
        settings.Store.Update(current => current with
        {
            About = current.About with
            {
                AutoUpdateWhenLaunch = AllowStatus.Yes,
                IsReceiveBetaVersion = AllowStatus.No,
                SkipVersionOnLaunch = string.Empty
            }
        });
        var navigation = new NavigationStub();
        var dialogs = new OrderedDialogService();
        using var handler = new ReleaseHandler();
        using var httpClient = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://api.github.test/")
        };
        using var clipboard = new ClipboardMonitorStub();
        using var viewModel = new MainWindowViewModel(
            navigation,
            new NotificationStub(),
            dialogs,
            settings.Store,
            clipboard,
            new SearchService(settings.Store, navigation),
            new VersionCheckerService(httpClient, "owner", "repo"),
            NullLogger<MainWindowViewModel>.Instance);

        var startup = viewModel.RunStartupDialogsAsync();
        await dialogs.LegacyDialogStarted.Task
            .WaitAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal([AppDialog.LegacyUpgrade], dialogs.Requests);
        Assert.Equal(0, handler.RequestCount);

        dialogs.CompleteLegacyDialog();
        await startup.ConfigureAwait(true);

        Assert.Equal(
            [AppDialog.LegacyUpgrade, AppDialog.NewVersionAvailable],
            dialogs.Requests);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task LifetimeCancellationStopsBeforeTheUpdateStepAfterDisposal()
    {
        using var settings = new TestSettingsStore();
        settings.Store.Update(current => current with
        {
            About = current.About with
            {
                AutoUpdateWhenLaunch = AllowStatus.Yes,
                IsReceiveBetaVersion = AllowStatus.No,
                SkipVersionOnLaunch = string.Empty
            }
        });
        var navigation = new NavigationStub();
        var dialogs = new OrderedDialogService();
        using var handler = new ReleaseHandler();
        using var httpClient = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://api.github.test/")
        };
        using var clipboard = new ClipboardMonitorStub();
        using var viewModel = new MainWindowViewModel(
            navigation,
            new NotificationStub(),
            dialogs,
            settings.Store,
            clipboard,
            new SearchService(settings.Store, navigation),
            new VersionCheckerService(httpClient, "owner", "repo"),
            NullLogger<MainWindowViewModel>.Instance);

        var startup = viewModel.RunStartupDialogsAsync();
        await dialogs.LegacyDialogStarted.Task
            .WaitAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        viewModel.Dispose();
        await startup.ConfigureAwait(true);

        Assert.Equal([AppDialog.LegacyUpgrade], dialogs.Requests);
        Assert.Equal(0, handler.RequestCount);
    }

    private sealed class OrderedDialogService : IAppDialogService
    {
        private readonly TaskCompletionSource<AppDialogResult> _legacyCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource LegacyDialogStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<AppDialog> Requests { get; } = [];

        public Task<AppDialogResult> ShowAsync(
            AppDialogRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request.Dialog);
            if (request.Dialog == AppDialog.LegacyUpgrade)
            {
                LegacyDialogStarted.TrySetResult();
                return _legacyCompletion.Task.WaitAsync(cancellationToken);
            }

            return Task.FromResult(new AppDialogResult(
                AppDialogOutcome.Accepted,
                new Dictionary<string, object?>(StringComparer.Ordinal)));
        }

        public void CompleteLegacyDialog()
        {
            _legacyCompletion.TrySetResult(new AppDialogResult(
                AppDialogOutcome.Accepted,
                new Dictionary<string, object?>(StringComparer.Ordinal)));
        }
    }

    private sealed class ReleaseHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {"tag_name":"v99.0.0","name":"future","body":"notes","prerelease":false,"html_url":"https://example.test/release"}
                    """)
            });
        }
    }

    private sealed class ClipboardMonitorStub : IClipboardMonitor
    {
        public event EventHandler<ClipboardTextChangedEventArgs>? Changed
        {
            add { }
            remove { }
        }

        public void Dispose()
        {
        }
    }

    private sealed class NotificationStub : IUserNotificationService
    {
        public event EventHandler<UserNotificationEventArgs>? NotificationRaised
        {
            add { }
            remove { }
        }

        public void Show(string message)
        {
        }
    }

    private sealed class NavigationStub : IAppNavigationService
    {
        public event EventHandler<AppNavigationChangedEventArgs>? NavigationChanged
        {
            add { }
            remove { }
        }

        public void Navigate(AppNavigationRequest request)
        {
        }

        public void NavigateRegion(
            AppNavigationRegion region,
            AppRoute route,
            IReadOnlyDictionary<string, object?>? parameters = null)
        {
        }

        public void ClearRegion(AppNavigationRegion region)
        {
        }

        public object? GetActiveView(AppNavigationRegion region) => null;

        public bool CanGoBack(AppNavigationRegion region) => false;

        public void GoBack(AppNavigationRegion region)
        {
        }
    }
}
