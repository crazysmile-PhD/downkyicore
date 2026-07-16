using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Xaml.Interactivity;
using DownKyi.Application.Desktop;
using DownKyi.Application.Lifetime;
using DownKyi.Composition;
using DownKyi.Core.Logging;
using DownKyi.Core.Settings;
using DownKyi.Core.Storage;
using DownKyi.CustomAction;
using DownKyi.CustomControl.AsyncImageLoader;
using DownKyi.CustomControl.AsyncImageLoader.Loaders;
using DownKyi.Desktop.Composition;
using DownKyi.Infrastructure.Downloads;
using DownKyi.Platform;
using DownKyi.Services;
using DownKyi.Services.Download;
using DownKyi.ViewModels;
using DownKyi.ViewModels.PageViewModels;
using DownKyi.ViewModels.Settings;
using DownKyi.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Desktop.Tests;

public sealed class UiSmokeTests
{
    [Fact]
    public void TypedRouterRestoresMainHistoryAndNavigationLifecycle()
    {
        EnsureHeadlessApplication();
        var created = new List<NavigationProbe>();
        using var navigation = new AvaloniaNavigationService(
            route =>
            {
                var probe = new NavigationProbe(route);
                created.Add(probe);
                return probe;
            },
            static action => action());

        navigation.Navigate(new AppNavigationRequest(AppRoute.Index, Parameter: "first"));
        navigation.Navigate(new AppNavigationRequest(AppRoute.Settings, AppRoute.Index, "second"));

        Assert.True(navigation.CanGoBack(AppNavigationRegion.Main));
        Assert.Same(created[1], navigation.GetActiveView(AppNavigationRegion.Main));
        Assert.Equal(1, created[0].NavigatedFromCount);
        Assert.Equal("first", created[0].LastContext?.Parameter);

        navigation.GoBack(AppNavigationRegion.Main);

        Assert.Same(created[0], navigation.GetActiveView(AppNavigationRegion.Main));
        Assert.False(navigation.CanGoBack(AppNavigationRegion.Main));
        Assert.Equal(2, created[0].NavigatedToCount);
        Assert.True(created[1].IsDisposed);
    }

    [Fact]
    public void TypedRouterReplacesAndDisposesNestedRegionContent()
    {
        EnsureHeadlessApplication();
        var created = new List<NavigationProbe>();
        using var navigation = new AvaloniaNavigationService(
            route =>
            {
                var probe = new NavigationProbe(route);
                created.Add(probe);
                return probe;
            },
            static action => action());

        navigation.NavigateRegion(AppNavigationRegion.Settings, AppRoute.SettingsBasic);
        navigation.NavigateRegion(AppNavigationRegion.Settings, AppRoute.SettingsNetwork);

        Assert.True(created[0].IsDisposed);
        Assert.Same(created[1], navigation.GetActiveView(AppNavigationRegion.Settings));
        Assert.False(navigation.CanGoBack(AppNavigationRegion.Settings));

        navigation.ClearRegion(AppNavigationRegion.Settings);

        Assert.True(created[1].IsDisposed);
        Assert.Null(navigation.GetActiveView(AppNavigationRegion.Settings));
    }

    [Fact]
    public async Task RealHostResolvesShellAndKeyViewsWithoutPrismRuntime()
    {
        AssertPrismRuntimeIsNotLoaded();
        EnsureHeadlessApplication();
        AssertVideoPageSelectionBehavior();

        var testDirectory = Path.Combine(Path.GetTempPath(), $"downkyi-host-smoke-{Guid.NewGuid():N}");
        var settingsStore = new SettingsStore(Path.Combine(testDirectory, "settings.json"));
        var logProvider = new ApplicationLogProvider(
            new ApplicationLogOptions(Path.Combine(testDirectory, "logs")));
        var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logProvider));

        try
        {
            using var host = DownKyiHost.Create(services =>
            {
                services.AddDownKyiDesktop(loggerFactory, logProvider);
                services.Replace(ServiceDescriptor.Singleton<ISettingsStore>(settingsStore));
                services.Replace(ServiceDescriptor.Singleton(
                    new SqliteDownloadTaskStoreOptions(Path.Combine(testDirectory, "downkyi.db"))));
            });

            var window = host.Services.GetRequiredService<MainWindow>();
            var mainViewModel = host.Services.GetRequiredService<MainWindowViewModel>();
            var imageLoader = host.Services.GetRequiredService<IAsyncImageLoader>();

            Assert.True(window.Width >= window.MinWidth);
            Assert.True(window.Height >= window.MinHeight);
            Assert.NotNull(window.Content);
            Assert.Same(mainViewModel, window.DataContext);
            Assert.IsType<DiskCachedWebImageLoader>(imageLoader);
            Assert.NotNull(host.Services.GetRequiredService<ViewIndexViewModel>());
            Assert.NotNull(host.Services.GetRequiredService<ViewVideoDetailViewModel>());
            Assert.NotNull(host.Services.GetRequiredService<ViewDownloadManagerViewModel>());
            Assert.NotNull(host.Services.GetRequiredService<ViewNetworkViewModel>());

            host.Services
                .GetRequiredService<IAppNavigationService>()
                .Navigate(new AppNavigationRequest(AppRoute.Index, Parameter: "smoke"));

            Assert.IsType<ViewIndexViewModel>(mainViewModel.MainContent);
            Assert.NotNull(Program.BuildAvaloniaApp());
            AssertPrismRuntimeIsNotLoaded();
        }
        finally
        {
            loggerFactory.Dispose();
            SynchronizationContext.SetSynchronizationContext(null);
            await logProvider.DisposeAsync();
            await settingsStore.DisposeAsync();
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
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

    private static void AssertPrismRuntimeIsNotLoaded()
    {
        Assert.DoesNotContain(
            AppDomain.CurrentDomain.GetAssemblies(),
            assembly => assembly.GetName().Name?.StartsWith("Prism", StringComparison.Ordinal) == true);
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

    private sealed class NavigationProbe(AppRoute route) : IAppNavigationAware, IDisposable
    {
        public AppRoute Route { get; } = route;

        public int NavigatedToCount { get; private set; }

        public int NavigatedFromCount { get; private set; }

        public bool IsDisposed { get; private set; }

        public AppNavigationContext? LastContext { get; private set; }

        public void OnNavigatedTo(AppNavigationContext context)
        {
            LastContext = context;
            NavigatedToCount++;
        }

        public void OnNavigatedFrom(AppNavigationContext context)
        {
            LastContext = context;
            NavigatedFromCount++;
        }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }
}
