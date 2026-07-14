using System;
using DownKyi.Application.Desktop;
using DownKyi.PrismExtension.Dialog;
using DownKyi.Services.Account;
using DownKyi.Services.Video;
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
        IDialogService dialogService,
        IClipboardService clipboardService)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(regionManager);
        ArgumentNullException.ThrowIfNull(eventAggregator);
        ArgumentNullException.ThrowIfNull(dialogService);
        ArgumentNullException.ThrowIfNull(clipboardService);

        services.AddSingleton(regionManager);
        services.AddSingleton(eventAggregator);
        services.AddSingleton(dialogService);
        services.AddSingleton(clipboardService);
        services.AddSingleton<IUserSessionCoordinator, UserSessionCoordinator>();
        services.AddTransient<IVideoDetailWorkflowCoordinator, VideoDetailWorkflowCoordinator>();
        services.AddSingleton<IVideoDetailDownloadCoordinator, VideoDetailDownloadCoordinator>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddTransient<ViewIndexViewModel>();
        services.AddTransient<ViewVideoDetailViewModel>();
        services.AddTransient<ViewDownloadManagerViewModel>();
        services.AddSingleton<MainWindow>();
        return services;
    }
}
