using System;
using DownKyi.PrismExtension.Dialog;
using DownKyi.ViewModels;
using DownKyi.Views;
using Microsoft.Extensions.DependencyInjection;
using Prism.Events;
using Prism.Navigation.Regions;

namespace DownKyi.Composition;

// Temporary bridge: PR 25-29 removes Prism and moves these registrations into DownKyi.Desktop.
internal static class LegacyDesktopComposition
{
    public static IServiceCollection AddLegacyDesktopShell(
        this IServiceCollection services,
        IRegionManager regionManager,
        IEventAggregator eventAggregator,
        IDialogService dialogService)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(regionManager);
        ArgumentNullException.ThrowIfNull(eventAggregator);
        ArgumentNullException.ThrowIfNull(dialogService);

        services.AddSingleton(regionManager);
        services.AddSingleton(eventAggregator);
        services.AddSingleton(dialogService);
        services.AddSingleton<MainWindowViewModel>();
        services.AddTransient<ViewIndexViewModel>();
        services.AddTransient<ViewVideoDetailViewModel>();
        services.AddTransient<ViewDownloadManagerViewModel>();
        services.AddSingleton<MainWindow>();
        return services;
    }
}
