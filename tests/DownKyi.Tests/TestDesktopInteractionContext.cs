using DownKyi.Application.Desktop;

namespace DownKyi.Tests;

internal sealed class TestDesktopInteractionContext : IDesktopInteractionContext
{
    public TestDesktopInteractionContext(IAppNavigationService? navigation = null)
    {
        Navigation = navigation ?? new TestNavigationService();
    }

    public IUserNotificationService Notifications { get; } = new TestNotificationService();

    public IAppNavigationService Navigation { get; }

    public IAppDialogService Dialogs { get; } = new TestDialogService();

    private sealed class TestNotificationService : IUserNotificationService
    {
        public event EventHandler<UserNotificationEventArgs>? NotificationRaised;

        public void Show(string message)
        {
            NotificationRaised?.Invoke(this, new UserNotificationEventArgs(message));
        }
    }

    private sealed class TestDialogService : IAppDialogService
    {
        public Task<AppDialogResult> ShowAsync(
            AppDialogRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new AppDialogResult(
                AppDialogOutcome.Canceled,
                new Dictionary<string, object?>()));
        }
    }
}
