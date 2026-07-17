using System;
using DownKyi.Application.Desktop;

namespace DownKyi.Platform;

internal sealed class DesktopInteractionContext : IDesktopInteractionContext
{
    public DesktopInteractionContext(
        IUserNotificationService notifications,
        IAppNavigationService navigation,
        IAppDialogService dialogs)
    {
        Notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        Navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
        Dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
    }

    public IUserNotificationService Notifications { get; }

    public IAppNavigationService Navigation { get; }

    public IAppDialogService Dialogs { get; }
}
