using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Xaml.Interactivity;
using DownKyi.Application.Desktop;
using DownKyi.Application.Lifetime;
using DownKyi.Composition;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.Settings;
using DownKyi.Core.Storage;
using DownKyi.CustomAction;
using DownKyi.Desktop.Composition;
using DownKyi.Platform;
using DownKyi.PrismExtension.Dialog;
using DownKyi.Services;
using DownKyi.Services.Download;
using DownKyi.ViewModels;
using DownKyi.ViewModels.PageViewModels;
using DownKyi.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Prism.Container.DryIoc;
using Prism.Events;
using Prism.Ioc;
using Prism.Navigation.Regions;
using DesktopDialogService = DownKyi.PrismExtension.Dialog.DialogService;

namespace DownKyi.Desktop.Tests;

public sealed class UiSmokeTests
{
    [Fact]
    public async Task RealHostResolvesShellAndKeyViewModelsWithoutGlobalContainerState()
    {
        ContainerLocator.ResetContainer();
        AssertPrismContainerIsUninitialized();

        EnsureHeadlessApplication();

        var app = Assert.IsType<SmokeTestApplication>(Avalonia.Application.Current);
        app.Initialize();
        AssertVideoPageSelectionBehavior();
        var prismContainer = new DryIocContainerExtension();
        var regionManager = new RegionManager();
        var eventAggregator = new EventAggregator();
        var dialogService = new DesktopDialogService(prismContainer);
        var settingsDirectory = Path.Combine(Path.GetTempPath(), $"downkyi-host-smoke-{Guid.NewGuid():N}");
        var settingsStore = new SettingsStore(Path.Combine(settingsDirectory, "settings.json"));
        using var host = DownKyiHost.Create(services =>
        {
            services.AddSingleton<IAddToDownloadServiceFactory>(new StubAddToDownloadServiceFactory());
            services.AddLegacyDesktopShell(
                regionManager,
                eventAggregator,
                dialogService,
                new StubClipboardService(),
                new StubPlatformLauncher(),
                settingsStore,
                new StubApplicationLifecycle(),
                new StubClipboardMonitor(),
                new DesktopNotificationService(),
                new StubNavigationService(),
                new StubAppDialogService());
        });

        try
        {
            await host.StartAsync(TestContext.Current.CancellationToken);
            var window = host.Services.GetRequiredService<MainWindow>();

            Assert.True(window.Width >= window.MinWidth);
            Assert.True(window.Height >= window.MinHeight);
            Assert.Same(host.Services.GetRequiredService<MainWindowViewModel>(), window.DataContext);
            Assert.NotNull(host.Services.GetRequiredService<ViewIndexViewModel>());
            Assert.NotNull(host.Services.GetRequiredService<ViewVideoDetailViewModel>());
            Assert.NotNull(host.Services.GetRequiredService<ViewDownloadManagerViewModel>());
            Assert.NotNull(Program.BuildAvaloniaApp());
            AssertPrismContainerIsUninitialized();
        }
        finally
        {
            try
            {
                await host.StopAsync(CancellationToken.None);
            }
            finally
            {
                prismContainer.Instance.Dispose();
                await settingsStore.DisposeAsync();
                if (Directory.Exists(settingsDirectory))
                {
                    Directory.Delete(settingsDirectory, recursive: true);
                }
            }
        }
    }

    [Fact]
    public void CreatingHostDoesNotRedirectExistingUserDataPaths()
    {
        var pathsBefore = GetUserDataPaths();

        using var host = DownKyiHost.Create();

        Assert.Equal(pathsBefore, GetUserDataPaths());
    }

    [Fact]
    public async Task StoppingHostSignalsSharedApplicationCancellation()
    {
        using var host = DownKyiHost.Create();
        var cancellation = host.Services.GetRequiredService<ApplicationCancellation>();
        await host.StartAsync(TestContext.Current.CancellationToken);

        await host.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(cancellation.ShutdownToken.IsCancellationRequested);
    }

    [Fact]
    public async Task StorageMaintenanceHostedServiceStopsWithApplicationCancellation()
    {
        using var cancellation = new ApplicationCancellation();
        var service = new StorageMaintenanceHostedService(
            cancellation,
            NullLogger<StorageMaintenanceHostedService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(cancellation.ShutdownToken.IsCancellationRequested);
    }

    private static void AssertVideoPageSelectionBehavior()
    {
        var firstPage = new VideoPage { Cid = 1, IsSelected = true };
        var secondPage = new VideoPage { Cid = 2 };
        var dataGrid = new DataGrid
        {
            ItemsSource = new[] { firstPage },
            SelectionMode = DataGridSelectionMode.Extended
        };
        var behaviors = Interaction.GetBehaviors(dataGrid);
        var behavior = new VideoPageSelectionBehavior();
        behaviors.Add(behavior);

        try
        {
            Assert.Contains(firstPage, dataGrid.SelectedItems.Cast<VideoPage>());
            Assert.True(behavior.IsSelectAll);

            dataGrid.ItemsSource = new[] { secondPage };
            secondPage.IsSelected = true;

            Assert.True(firstPage.IsSelected);
            Assert.Contains(secondPage, dataGrid.SelectedItems.Cast<VideoPage>());
            Assert.True(behavior.IsSelectAll);

            secondPage.IsSelected = false;

            Assert.Empty(dataGrid.SelectedItems);
            Assert.False(behavior.IsSelectAll);
        }
        finally
        {
            behaviors.Remove(behavior);
        }
    }

    private static string[] GetUserDataPaths()
    {
        return
        [
            StorageManager.GetDbPath(),
            StorageManager.GetSettings(),
            StorageManager.GetLogin(),
            StorageManager.GetAriaDir()
        ];
    }

    private static void AssertPrismContainerIsUninitialized()
    {
        Assert.Throws<InvalidOperationException>(() => ContainerLocator.Container);
    }

    private static void EnsureHeadlessApplication()
    {
        if (Avalonia.Application.Current != null)
        {
            return;
        }

        AppBuilder
            .Configure<SmokeTestApplication>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .SetupWithoutStarting();
    }

    private sealed class SmokeTestApplication : Avalonia.Application
    {
    }

    private sealed class StubAddToDownloadServiceFactory : IAddToDownloadServiceFactory
    {
        public IAddToDownloadSession Create(PlayStreamType streamType)
        {
            throw new NotSupportedException();
        }

        public IAddToDownloadSession Create(string id, PlayStreamType streamType)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class StubClipboardService : IClipboardService
    {
        public Task SetTextAsync(string text, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class StubClipboardMonitor : IClipboardMonitor
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

    private sealed class StubApplicationLifecycle : IApplicationLifecycle
    {
        public Task RequestShutdownAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task ExitAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<bool> RestartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        }
    }

    private sealed class StubNavigationService : IAppNavigationService
    {
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

        public object? GetActiveView(AppNavigationRegion region)
        {
            return null;
        }
    }

    private sealed class StubAppDialogService : IAppDialogService
    {
        public Task<AppDialogResult> ShowAsync(
            AppDialogRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new AppDialogResult(
                AppDialogOutcome.Canceled,
                new Dictionary<string, object?>()));
        }
    }

    private sealed class StubPlatformLauncher : IPlatformLauncher
    {
        public Task<bool> OpenFileAsync(string path, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<bool> OpenFolderAsync(string path, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<bool> OpenUriAsync(Uri uri, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }
    }
}
