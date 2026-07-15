namespace DownKyi.Application.Desktop;

public sealed class UserNotificationEventArgs(string message) : EventArgs
{
    public string Message { get; } = message;
}

public interface IUserNotificationService
{
    event EventHandler<UserNotificationEventArgs>? NotificationRaised;

    void Show(string message);
}
