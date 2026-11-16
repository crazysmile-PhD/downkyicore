using System;
using DownKyi.Application.Desktop;
using Microsoft.Extensions.DependencyInjection;

namespace DownKyi.Platform;

internal static class DesktopInteractionComposition
{
    public static IServiceCollection AddDesktopInteractions(this IServiceCollection services)
    {
        services.AddSingleton<NavigationViewModelFactory>();
        services.AddSingleton<IAppNavigationService, AvaloniaNavigationService>();
        services.AddSingleton<DialogContentFactory>();
        services.AddSingleton<IAppDialogService, AvaloniaDialogService>();
        services.AddSingleton<IUserNotificationService, DesktopNotificationService>();
        services.AddSingleton<IDesktopInteractionContext, DesktopInteractionContext>();

        // The local factories own identity-to-type mappings; registration follows them.
        foreach (var route in Enum.GetValues<AppRoute>())
        {
            services.AddTransient(NavigationViewModelFactory.GetViewModelType(route));
        }

        foreach (var dialog in Enum.GetValues<AppDialog>())
        {
            var (view, viewModel) = DialogContentFactory.GetDialogTypes(dialog);
            services.AddTransient(view);
            services.AddTransient(viewModel);
        }

        return services;
    }
}
