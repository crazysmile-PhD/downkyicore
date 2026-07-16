using DownKyi.Application.Desktop;
using DownKyi.Application.Lifetime;
using DownKyi.Core.Settings;
using DownKyi.Services.Settings;
using DownloaderSetting = DownKyi.Core.Settings.Downloader;

namespace DownKyi.Tests;

public sealed class NetworkSettingsCoordinatorTests
{
    [Fact]
    public void OptionsAreStableImmutableRanges()
    {
        var options = NetworkSettingsOptions.Default;

        Assert.Equal(Enumerable.Range(1, 10), options.MaxCurrentDownloads);
        Assert.Equal(Enumerable.Range(1, 16), options.Splits);
        Assert.Equal(["DEBUG", "INFO", "NOTICE", "WARN", "ERROR"], options.AriaLogLevels);
        Assert.Equal([1, 2, 4, 8, 10, 16, 32, 64], options.AriaMinSplitSizes);
        Assert.Equal(["NONE", "PREALLOC", "FALLOC"], options.AriaFileAllocations);
    }

    [Fact]
    public void ApplyPersistsValidatedSettingsAndReportsTheResult()
    {
        using var settings = new TestSettingsStore();
        var notifications = new RecordingNotificationService();
        var coordinator = CreateCoordinator(settings, notifications: notifications);

        var applied = coordinator.Apply(
            network => network with { MaxCurrentDownloads = 6 },
            network => network.MaxCurrentDownloads == 6,
            showFeedback: true);

        Assert.True(applied);
        Assert.Equal(6, settings.Store.Current.Network.MaxCurrentDownloads);
        Assert.Single(notifications.Messages);
    }

    [Fact]
    public async Task InitializationUpdateSuppressesFeedbackAndRestartPrompt()
    {
        using var settings = new TestSettingsStore();
        var notifications = new RecordingNotificationService();
        var dialogs = new StubDialogService(AppDialogOutcome.Accepted);
        var lifecycle = new StubApplicationLifecycle();
        var coordinator = CreateCoordinator(settings, notifications, dialogs, lifecycle);

        var applied = await coordinator.ApplyWithRestartPromptAsync(
            network => network with { Downloader = DownloaderSetting.Aria },
            network => network.Downloader == DownloaderSetting.Aria,
            showFeedback: false,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(applied);
        Assert.Empty(notifications.Messages);
        Assert.Equal(0, dialogs.ShowCount);
        Assert.Equal(0, lifecycle.RestartCount);
    }

    [Fact]
    public async Task RejectedValidatedValueDoesNotPromptForRestart()
    {
        using var settings = new TestSettingsStore();
        var notifications = new RecordingNotificationService();
        var dialogs = new StubDialogService(AppDialogOutcome.Accepted);
        var lifecycle = new StubApplicationLifecycle();
        var coordinator = CreateCoordinator(settings, notifications, dialogs, lifecycle);

        var applied = await coordinator.ApplyWithRestartPromptAsync(
            network => network with { MaxCurrentDownloads = 0 },
            network => network.MaxCurrentDownloads == 0,
            showFeedback: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(applied);
        Assert.NotEqual(0, settings.Store.Current.Network.MaxCurrentDownloads);
        Assert.Single(notifications.Messages);
        Assert.Equal(0, dialogs.ShowCount);
        Assert.Equal(0, lifecycle.RestartCount);
    }

    [Fact]
    public async Task AcceptedRestartPromptRequestsOneRestart()
    {
        using var settings = new TestSettingsStore();
        var dialogs = new StubDialogService(AppDialogOutcome.Accepted);
        var lifecycle = new StubApplicationLifecycle();
        var coordinator = CreateCoordinator(
            settings,
            dialogs: dialogs,
            lifecycle: lifecycle);

        var applied = await coordinator.ApplyWithRestartPromptAsync(
            network => network with { NetworkProxy = NetworkProxy.System },
            network => network.NetworkProxy == NetworkProxy.System,
            showFeedback: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(applied);
        Assert.Equal(1, dialogs.ShowCount);
        Assert.Equal(1, lifecycle.RestartCount);
    }

    private static NetworkSettingsCoordinator CreateCoordinator(
        TestSettingsStore settings,
        RecordingNotificationService? notifications = null,
        StubDialogService? dialogs = null,
        StubApplicationLifecycle? lifecycle = null)
    {
        return new NetworkSettingsCoordinator(
            settings.Store,
            notifications ?? new RecordingNotificationService(),
            dialogs ?? new StubDialogService(AppDialogOutcome.Canceled),
            lifecycle ?? new StubApplicationLifecycle());
    }

    private sealed class RecordingNotificationService : IUserNotificationService
    {
        public event EventHandler<UserNotificationEventArgs>? NotificationRaised;

        public List<string> Messages { get; } = [];

        public void Show(string message)
        {
            Messages.Add(message);
            NotificationRaised?.Invoke(this, new UserNotificationEventArgs(message));
        }
    }

    private sealed class StubDialogService(AppDialogOutcome outcome) : IAppDialogService
    {
        public int ShowCount { get; private set; }

        public Task<AppDialogResult> ShowAsync(
            AppDialogRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ShowCount++;
            return Task.FromResult(new AppDialogResult(
                outcome,
                new Dictionary<string, object?>()));
        }
    }

    private sealed class StubApplicationLifecycle : IApplicationLifecycle
    {
        public int RestartCount { get; private set; }

        public Task RequestShutdownAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task ExitAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<bool> RestartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RestartCount++;
            return Task.FromResult(true);
        }
    }
}
