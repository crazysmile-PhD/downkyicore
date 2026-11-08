using System.Net;
using DownKyi.Application.Desktop;
using DownKyi.Application.Downloads;
using DownKyi.Application.Media;
using DownKyi.Application.Settings;
using DownKyi.Infrastructure.Versioning;
using DownKyi.Platform;
using DownKyi.Services;
using DownKyi.Services.Download;
using DownKyi.Services.Migration;
using DownKyi.Services.Startup;
using DownKyi.ViewModels;
using DownKyi.ViewModels.Dialogs;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class StartupDialogOrderingTests
{
    [Fact]
    public void StartupClipboardInputAutomaticallyContinuesAfterActivation()
    {
        using var settings = new TestSettingsStore();
        settings.Store.Update(current => current with
        {
            About = current.About with { AutoUpdateWhenLaunch = AllowStatus.No }
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
            new LegacyUpgradeCoordinatorStub(required: false),
            new RuntimeAvailabilityStub(),
            NullLogger<MainWindowViewModel>.Instance);

        viewModel.Activate(
            pendingStartupInput: "BV17x411w7KC",
            pendingStartupInputFromClipboard: true);

        Assert.Collection(
            navigation.Requests,
            request => Assert.Equal(AppRoute.Index, request.Route),
            request =>
            {
                Assert.Equal(AppRoute.VideoDetail, request.Route);
                var payload = Assert.IsType<VideoNavigationPayload>(request.Parameter);
                Assert.True(payload.SuppressDuplicate);
                Assert.Equal(
                    "BV17x411w7KC",
                    Assert.IsType<BilibiliBvidInput>(payload.Input).Bvid);
            });
    }

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
            new LegacyUpgradeCoordinatorStub(),
            new RuntimeAvailabilityStub(),
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
            new LegacyUpgradeCoordinatorStub(),
            new RuntimeAvailabilityStub(),
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

    [Fact]
    public async Task DownloadFailureIsShownOnceAfterLegacyDialogAndBeforeUpdateDialog()
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
        var failure = new InvalidOperationException("Synthetic download bootstrap failure.");
        using var viewModel = new MainWindowViewModel(
            navigation,
            new NotificationStub(),
            dialogs,
            settings.Store,
            clipboard,
            new SearchService(settings.Store, navigation),
            new VersionCheckerService(httpClient, "owner", "repo"),
            new LegacyUpgradeCoordinatorStub(),
            new RuntimeAvailabilityStub(DownloadRuntimeStartupOutcome.Faulted(failure)),
            NullLogger<MainWindowViewModel>.Instance);

        var startup = viewModel.RunStartupDialogsAsync();
        await dialogs.LegacyDialogStarted.Task
            .WaitAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal([AppDialog.LegacyUpgrade], dialogs.Requests);

        dialogs.CompleteLegacyDialog();
        await startup.ConfigureAwait(true);

        Assert.Equal(
            [
                AppDialog.LegacyUpgrade,
                AppDialog.StartupDiagnostics,
                AppDialog.NewVersionAvailable
            ],
            dialogs.Requests);
        var diagnostic = Assert.Single(dialogs.StartupDiagnostics!);
        Assert.Equal(StartupDiagnosticSource.DownloadBootstrap, diagnostic.Source);
        Assert.Same(failure, diagnostic.Failure);
    }

    [Fact]
    public async Task LegacyFailureDoesNotBlockAlreadyDetectedDownloadFailure()
    {
        using var settings = new TestSettingsStore();
        settings.Store.Update(current => current with
        {
            About = current.About with
            {
                AutoUpdateWhenLaunch = AllowStatus.No
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
        var migrationFailure = new InvalidOperationException("Synthetic migration failure.");
        var downloadFailure = new InvalidOperationException("Synthetic download failure.");
        var availability = new RuntimeAvailabilityStub(
            DownloadRuntimeStartupOutcome.Faulted(downloadFailure));
        using var viewModel = new MainWindowViewModel(
            navigation,
            new NotificationStub(),
            dialogs,
            settings.Store,
            clipboard,
            new SearchService(settings.Store, navigation),
            new VersionCheckerService(httpClient, "owner", "repo"),
            new LegacyUpgradeCoordinatorStub(),
            availability,
            NullLogger<MainWindowViewModel>.Instance);

        var startup = viewModel.RunStartupDialogsAsync();
        await dialogs.LegacyDialogStarted.Task
            .WaitAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(1, availability.WaitCallCount);
        Assert.Equal([AppDialog.LegacyUpgrade], dialogs.Requests);

        dialogs.CompleteLegacyDialogWithFailure(migrationFailure);
        await startup.ConfigureAwait(true);

        Assert.Equal(
            [AppDialog.LegacyUpgrade, AppDialog.StartupDiagnostics],
            dialogs.Requests);
        Assert.Collection(
            dialogs.StartupDiagnostics!,
            migration =>
            {
                Assert.Equal(StartupDiagnosticSource.LegacyMigration, migration.Source);
                Assert.Same(migrationFailure, migration.Failure);
            },
            download =>
            {
                Assert.Equal(StartupDiagnosticSource.DownloadBootstrap, download.Source);
                Assert.Same(downloadFailure, download.Failure);
            });
    }

    [Fact]
    public async Task StartupWithoutLegacyDataSkipsMigrationDialog()
    {
        using var settings = new TestSettingsStore();
        settings.Store.Update(current => current with
        {
            About = current.About with
            {
                AutoUpdateWhenLaunch = AllowStatus.No
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
            new LegacyUpgradeCoordinatorStub(required: false),
            new RuntimeAvailabilityStub(),
            NullLogger<MainWindowViewModel>.Instance);

        await viewModel.RunStartupDialogsAsync().ConfigureAwait(true);

        Assert.Empty(dialogs.Requests);
        Assert.Equal(0, handler.RequestCount);
    }

    private sealed class OrderedDialogService : IAppDialogService
    {
        private readonly TaskCompletionSource<AppDialogResult> _legacyCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource LegacyDialogStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<AppDialog> Requests { get; } = [];

        public IReadOnlyList<StartupDiagnostic>? StartupDiagnostics { get; private set; }

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

            if (request.Dialog == AppDialog.StartupDiagnostics)
            {
                StartupDiagnostics = Assert.IsAssignableFrom<IReadOnlyList<StartupDiagnostic>>(
                    request.Parameters?[StartupDiagnosticsDialogViewModel.DiagnosticsParameter]);
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

        public void CompleteLegacyDialogWithFailure(Exception failure)
        {
            _legacyCompletion.TrySetResult(new AppDialogResult(
                AppDialogOutcome.Rejected,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [ViewUpgradingDialogViewModel.FailureResultParameter] =
                        new LegacyUpgradeResult(
                            LegacyUpgradeOutcome.Failed,
                            "migration failed",
                            failure)
                }));
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

    private sealed class RuntimeAvailabilityStub : IDownloadRuntimeAvailability
    {
        private readonly DownloadRuntimeStartupOutcome _outcome;

        public int WaitCallCount { get; private set; }

        public RuntimeAvailabilityStub()
            : this(DownloadRuntimeStartupOutcome.Ready())
        {
        }

        public RuntimeAvailabilityStub(DownloadRuntimeStartupOutcome outcome)
        {
            _outcome = outcome;
        }

        public void EnsureAcceptingTasks()
        {
        }

        public Task<DownloadRuntimeStartupOutcome> WaitForStartupOutcomeAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WaitCallCount++;
            return Task.FromResult(_outcome);
        }
    }

    private sealed class LegacyUpgradeCoordinatorStub(bool required = true)
        : ILegacyUpgradeCoordinator
    {
        public bool IsUpgradeRequired() => required;

        public Task<LegacyUpgradeResult> UpgradeAsync(
            IProgress<LegacyUpgradeProgress> progress,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class NavigationStub : IAppNavigationService
    {
        public List<AppNavigationRequest> Requests { get; } = [];

        public event EventHandler<AppNavigationChangedEventArgs>? NavigationChanged
        {
            add { }
            remove { }
        }

        public void Navigate(AppNavigationRequest request)
        {
            Requests.Add(request);
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
