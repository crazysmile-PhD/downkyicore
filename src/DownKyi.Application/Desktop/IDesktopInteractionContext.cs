namespace DownKyi.Application.Desktop;

public interface IDesktopInteractionContext
{
    IUserNotificationService Notifications { get; }

    IAppNavigationService Navigation { get; }

    IAppDialogService Dialogs { get; }
}
