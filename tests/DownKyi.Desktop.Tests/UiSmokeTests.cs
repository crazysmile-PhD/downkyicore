using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Headless;
using DownKyi.ViewModels;
using DownKyi.Views;
using Prism.Container.DryIoc;
using Prism.Ioc;
using Prism.Mvvm;
using Prism.Navigation.Regions;

namespace DownKyi.Desktop.Tests;

public sealed class UiSmokeTests
{
    [Fact]
    public void AppBuilderAndMainWindowCanInitializeOnHeadlessPlatform()
    {
        var builder = AppBuilder
            .Configure<SmokeTestApplication>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
        builder.SetupWithoutStarting();

        var app = Assert.IsType<SmokeTestApplication>(Application.Current);
        app.Initialize();
        var container = new DryIocContainerExtension();
        container.RegisterSingleton<IRegionManager, RegionManager>();
        ContainerLocator.SetContainerExtension(container);
        ViewModelLocationProvider.SetDefaultViewModelFactory(RuntimeHelpers.GetUninitializedObject);
        try
        {
            var window = new MainWindow();

            Assert.True(window.Width >= window.MinWidth);
            Assert.True(window.Height >= window.MinHeight);
            Assert.IsType<MainWindowViewModel>(window.DataContext);
            Assert.NotNull(Program.BuildAvaloniaApp());
        }
        finally
        {
            ContainerLocator.ResetContainer();
            container.Instance.Dispose();
        }
    }

    private sealed class SmokeTestApplication : Application
    {
    }
}
