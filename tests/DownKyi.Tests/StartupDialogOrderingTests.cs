using System.Net;
using DownKyi.Application.Desktop;
using DownKyi.Application.Downloads;
using DownKyi.Application.Lifetime;
using DownKyi.Core.Settings;
using DownKyi.Platform;
using DownKyi.Services;
using DownKyi.Services.Download;
using DownKyi.Services.Migration;
using DownKyi.Services.Settings;
using DownKyi.Services.Startup;
using DownKyi.ViewModels;
using DownKyi.ViewModels.Dialogs;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class StartupDialogOrderingTests
{
    [Fact]
    public void ReopeningTheWindowDoesNotRepeatLoadedInitialization()
    {
        using var settings = new TestSettingsStore();
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
            new RuntimeAvailabilityStub(),
            CreateRecoveryPresenter(dialogs),
            CreateNetworkSettingsCoordinator(settings.Store, dialogs),
            NullLogger<MainWindowViewModel>.Instance);

        viewModel.PrepareForFirstShow();
        viewModel.LoadedCommand!.Execute(null);
        viewModel.LoadedCommand.Execute(null);

        Assert.Equal(1, navigation.NavigateCallCount);
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
            new RuntimeAvailabilityStub(),
            CreateRecoveryPresenter(dialogs),
            CreateNetworkSettingsCoordinator(settings.Store, dialogs),
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
            new RuntimeAvailabilityStub(),
            CreateRecoveryPresenter(dialogs),
            CreateNetworkSettingsCoordinator(settings.Store, dialogs),
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
            new RuntimeAvailabilityStub(DownloadRuntimeStartupOutcome.Faulted(failure)),
            CreateRecoveryPresenter(dialogs),
            CreateNetworkSettingsCoordinator(settings.Store, dialogs),
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
            availability,
            CreateRecoveryPresenter(dialogs),
            CreateNetworkSettingsCoordinator(settings.Store, dialogs),
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
    public async Task MissingAriaFallbackRoutesThroughNetworkSettingsOwner()
    {
        using var settings = new TestSettingsStore();
        settings.Store.Update(current => current with
        {
            About = current.About with { AutoUpdateWhenLaunch = AllowStatus.No },
            Network = current.Network with { Downloader = Downloader.Aria }
        });
        var navigation = new NavigationStub();
        var dialogs = new OrderedDialogService
        {
            StartupDiagnosticsResultFactory = diagnostics => new AppDialogResult(
                AppDialogOutcome.Accepted,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [StartupDiagnosticsDialogViewModel.BuiltInFallbackDiagnosticParameter] =
                        Assert.Single(diagnostics)
                })
        };
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
            new RuntimeAvailabilityStub(DownloadRuntimeStartupOutcome.Faulted(
                new FileNotFoundException("aria2 executable missing"))),
            CreateRecoveryPresenter(dialogs),
            CreateNetworkSettingsCoordinator(settings.Store, dialogs),
            NullLogger<MainWindowViewModel>.Instance);

        var startup = viewModel.RunStartupDialogsAsync();
        await dialogs.LegacyDialogStarted.Task
            .WaitAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        dialogs.CompleteLegacyDialog();
        await startup.ConfigureAwait(true);

        Assert.Equal(Downloader.BuiltIn, settings.Store.Current.Network.Downloader);
    }

    private static DownloadStoreRecoveryPresenter CreateRecoveryPresenter(
        IAppDialogService dialogs) =>
        new(
            dialogs,
            new DownloadStoreRecoveryStub(),
            new ApplicationLifecycleStub(),
            NullLogger<DownloadStoreRecoveryPresenter>.Instance);

    private static NetworkSettingsCoordinator CreateNetworkSettingsCoordinator(
        ISettingsStore settingsStore,
        IAppDialogService dialogs) =>
        new NetworkSettingsCoordinator(
            settingsStore,
            new NotificationStub(),
            dialogs,
            new ApplicationLifecycleStub());

    private sealed class OrderedDialogService : IAppDialogService
    {
        private readonly TaskCompletionSource<AppDialogResult> _legacyCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource LegacyDialogStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<AppDialog> Requests { get; } = [];

        public IReadOnlyList<StartupDiagnostic>? StartupDiagnostics { get; private set; }

        public Func<IReadOnlyList<StartupDiagnostic>, AppDialogResult>?
            StartupDiagnosticsResultFactory
        { get; init; }

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
                if (StartupDiagnosticsResultFactory != null)
                {
                    return Task.FromResult(
                        StartupDiagnosticsResultFactory(StartupDiagnostics));
                }
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

    private sealed class DownloadStoreRecoveryStub : IDownloadStoreRecovery
    {
        public Task BackupAndResetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class ApplicationLifecycleStub : IApplicationLifecycle
    {
        public Task RequestShutdownAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ExitAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> RestartAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class NavigationStub : IAppNavigationService
    {
        public int NavigateCallCount { get; private set; }

        public event EventHandler<AppNavigationChangedEventArgs>? NavigationChanged
        {
            add { }
            remove { }
        }

        public void Navigate(AppNavigationRequest request)
        {
            NavigateCallCount++;
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
