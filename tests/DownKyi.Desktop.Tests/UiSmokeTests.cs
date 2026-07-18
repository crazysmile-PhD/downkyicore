using Avalonia;
using Avalonia.Headless;
using DownKyi.Application.Lifetime;
using DownKyi.Composition;
using DownKyi.Core.Storage;
using DownKyi.Desktop.Composition;
using DownKyi.Events;
using DownKyi.Images;
using DownKyi.PrismExtension.Dialog;
using DownKyi.ViewModels;
using DownKyi.Views;
using Microsoft.Extensions.DependencyInjection;
using Prism.Container.DryIoc;
using Prism.Events;
using Prism.Ioc;
using Prism.Navigation.Regions;
using DesktopDialogService = DownKyi.PrismExtension.Dialog.DialogService;

namespace DownKyi.Desktop.Tests;

public sealed class UiSmokeTests
{
    [Fact]
    public void StandardReturnNavigationUsesTheRegionHistory()
    {
        Assert.True(MainWindowViewModel.IsHistoryBackRequest(new NavigationParam
        {
            ViewName = ViewVideoDetailViewModel.Tag
        }));

        Assert.False(MainWindowViewModel.IsHistoryBackRequest(new NavigationParam
        {
            ViewName = ViewIndexViewModel.Tag,
            Parameter = "logout"
        }));
    }

    [Fact]
    public void NavigationIconsDoNotShareMutableFillState()
    {
        var first = NavigationIcon.Instance().ArrowBack;
        var second = NavigationIcon.Instance().ArrowBack;

        first.Fill = "#FFFFFFFF";

        Assert.NotSame(first, second);
        Assert.Equal("#FF000000", second.Fill);
    }

    [Fact]
    public async Task RealHostResolvesShellAndKeyViewModelsWithoutGlobalContainerState()
    {
        ContainerLocator.ResetContainer();
        AssertPrismContainerIsUninitialized();

        var builder = AppBuilder
            .Configure<SmokeTestApplication>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
        builder.SetupWithoutStarting();

        var app = Assert.IsType<SmokeTestApplication>(Avalonia.Application.Current);
        app.Initialize();
        var prismContainer = new DryIocContainerExtension();
        var regionManager = new RegionManager();
        var eventAggregator = new EventAggregator();
        var dialogService = new DesktopDialogService(prismContainer);
        using var host = DownKyiHost.Create(services => services.AddLegacyDesktopShell(
            regionManager,
            eventAggregator,
            dialogService));

        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
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
            await host.StopAsync(TestContext.Current.CancellationToken);
            prismContainer.Instance.Dispose();
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

    private sealed class SmokeTestApplication : Avalonia.Application
    {
    }
}
