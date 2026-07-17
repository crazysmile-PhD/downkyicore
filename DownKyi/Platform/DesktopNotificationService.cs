using System;
using DownKyi.Application.Desktop;

namespace DownKyi.Platform;

internal sealed class DesktopNotificationService : IUserNotificationService
{
    public event EventHandler<UserNotificationEventArgs>? NotificationRaised;

    public void Show(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        NotificationRaised?.Invoke(this, new UserNotificationEventArgs(message));
    }
}
