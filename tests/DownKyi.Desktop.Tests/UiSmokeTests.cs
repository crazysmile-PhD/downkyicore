using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
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
    public Task PublicFavoritesBackArrowRemainsVisibleInLightAndDarkThemes()
    {
        return HeadlessUiTestHost.RunAsync(() =>
        {
            var application = EnsureProductThemeResources();
            var originalTheme = application.RequestedThemeVariant;
            var view = new ViewPublicFavorites();
            var window = new Window
            {
                Content = view,
                Width = 840,
                Height = 620
            };

            try
            {
                window.Show();
                var arrow = view.FindControl<Avalonia.Controls.Shapes.Path>("BackArrowPath");
                Assert.NotNull(arrow);

                application.RequestedThemeVariant = ThemeVariant.Light;
                window.UpdateLayout();
                Assert.Equal(Colors.Black, Assert.IsType<SolidColorBrush>(arrow.Fill).Color);

                application.RequestedThemeVariant = ThemeVariant.Dark;
                window.UpdateLayout();
                Assert.Equal(Colors.White, Assert.IsType<SolidColorBrush>(arrow.Fill).Color);
            }
            finally
            {
                application.RequestedThemeVariant = originalTheme;
                window.Close();
            }
        });
    }

    [Fact]
    public Task TypedRouterShrinksThreeLevelHistoryAndRestoresOriginalInstances()
    {
        return HeadlessUiTestHost.RunAsync(() =>
        {
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
            navigation.Navigate(new AppNavigationRequest(AppRoute.Toolbox, AppRoute.Settings, "third"));

            Assert.True(navigation.CanGoBack(AppNavigationRegion.Main));
            Assert.Same(created[2], navigation.GetActiveView(AppNavigationRegion.Main));
            Assert.Equal(1, created[0].NavigatedFromCount);
            Assert.Equal("first", created[0].LastContext?.Parameter);
            Assert.Equal(1, created[1].NavigatedFromCount);

            navigation.GoBack(AppNavigationRegion.Main);

            Assert.Same(created[1], navigation.GetActiveView(AppNavigationRegion.Main));
            Assert.True(navigation.CanGoBack(AppNavigationRegion.Main));
            Assert.Equal(2, created[1].NavigatedToCount);
            Assert.True(created[2].IsDisposed);
            Assert.False(created[0].IsDisposed);

            navigation.GoBack(AppNavigationRegion.Main);

            Assert.Same(created[0], navigation.GetActiveView(AppNavigationRegion.Main));
            Assert.False(navigation.CanGoBack(AppNavigationRegion.Main));
            Assert.Equal(2, created[0].NavigatedToCount);
            Assert.True(created[1].IsDisposed);
            Assert.Equal(3, created.Count);
        });
    }

    [Fact]
    public Task UserSpaceFavoritesBackPathRestoresOriginalUserSpaceInstance()
    {
        return HeadlessUiTestHost.RunAsync(() =>
        {
            var created = new List<NavigationProbe>();
            using var navigation = new AvaloniaNavigationService(
                route =>
                {
                    var probe = new NavigationProbe(route);
                    created.Add(probe);
                    return probe;
                },
                static action => action());

            navigation.Navigate(new AppNavigationRequest(AppRoute.UserSpace, Parameter: 42L));
            var originalUserSpace = navigation.GetActiveView(AppNavigationRegion.Main);
            navigation.Navigate(new AppNavigationRequest(
                AppRoute.UserSpaceFavorites,
                AppRoute.UserSpace,
                Parameter: "folders"));
            var originalFolders = navigation.GetActiveView(AppNavigationRegion.Main);
            navigation.Navigate(new AppNavigationRequest(
                AppRoute.PublicFavorites,
                AppRoute.UserSpace,
                Parameter: 7L));

            navigation.GoBack(AppNavigationRegion.Main);
            Assert.Same(originalFolders, navigation.GetActiveView(AppNavigationRegion.Main));
            Assert.True(navigation.CanGoBack(AppNavigationRegion.Main));
            Assert.True(created[2].IsDisposed);

            navigation.GoBack(AppNavigationRegion.Main);
            Assert.Same(originalUserSpace, navigation.GetActiveView(AppNavigationRegion.Main));
            Assert.False(navigation.CanGoBack(AppNavigationRegion.Main));
            Assert.True(created[1].IsDisposed);
            Assert.Equal(3, created.Count);
        });
    }

    [Fact]
    public Task TypedRouterReplacesAndDisposesNestedRegionContent()
    {
        return HeadlessUiTestHost.RunAsync(() =>
        {
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
        });
    }

    [Fact]
    public async Task RealHostResolvesShellAndKeyViewsWithoutPrismRuntime()
    {
        await HeadlessUiTestHost.RunAsync(async () =>
        {
            AssertPrismRuntimeIsNotLoaded();
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
                Assert.NotNull(host.Services.GetRequiredService<DownKyi.ViewModels.UserSpace.ViewFavoritesViewModel>());

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
                await logProvider.DisposeAsync().ConfigureAwait(true);
                await settingsStore.DisposeAsync().ConfigureAwait(true);
                if (Directory.Exists(testDirectory))
                {
                    Directory.Delete(testDirectory, recursive: true);
                }
            }
        });
    }

    [Fact]
    public async Task MainWindowStillClosesWhenShutdownRequestFaults()
    {
        await HeadlessUiTestHost.RunAsync(async () =>
        {
            var testDirectory = Path.Combine(Path.GetTempPath(), $"downkyi-close-smoke-{Guid.NewGuid():N}");
            var settingsStore = new SettingsStore(Path.Combine(testDirectory, "settings.json"));
            var logProvider = new ApplicationLogProvider(
                new ApplicationLogOptions(Path.Combine(testDirectory, "logs")));
            var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logProvider));
            var lifecycle = new ThrowingApplicationLifecycle();

            try
            {
                using var host = DownKyiHost.Create(services =>
                {
                    services.AddDownKyiDesktop(loggerFactory, logProvider);
                    services.Replace(ServiceDescriptor.Singleton<ISettingsStore>(settingsStore));
                    services.Replace(ServiceDescriptor.Singleton<IApplicationLifecycle>(lifecycle));
                    services.Replace(ServiceDescriptor.Singleton(
                        new SqliteDownloadTaskStoreOptions(Path.Combine(testDirectory, "downkyi.db"))));
                });
                var window = host.Services.GetRequiredService<MainWindow>();
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                window.Closed += (_, _) => closed.TrySetResult();

                window.Show();
                window.Close();

                await closed.Task
                    .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)
                    .ConfigureAwait(true);
                Assert.Equal(1, lifecycle.ShutdownRequestCount);
                Assert.False(window.IsVisible);
            }
            finally
            {
                loggerFactory.Dispose();
                await logProvider.DisposeAsync().ConfigureAwait(true);
                await settingsStore.DisposeAsync().ConfigureAwait(true);
                if (Directory.Exists(testDirectory))
                {
                    Directory.Delete(testDirectory, recursive: true);
                }
            }
        });
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

    private static Avalonia.Application EnsureProductThemeResources()
    {
        var application = Avalonia.Application.Current
            ?? throw new InvalidOperationException("Avalonia application is not initialized.");
        if (application.TryGetResource("ImageBtnStyle", ThemeVariant.Default, out _))
        {
            return application;
        }

        application.Resources.MergedDictionaries.Add(new ResourceInclude(
            new Uri("avares://DownKyi.Desktop.Tests/"))
        {
            Source = new Uri("avares://DownKyi/Themes/ThemeDefault.axaml")
        });
        return application;
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

    private sealed class ThrowingApplicationLifecycle : IApplicationLifecycle
    {
        public int ShutdownRequestCount { get; private set; }

        public Task RequestShutdownAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ShutdownRequestCount++;
            return Task.FromException(new InvalidOperationException("Expected shutdown failure."));
        }

        public Task ExitAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<bool> RestartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(false);
        }
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
