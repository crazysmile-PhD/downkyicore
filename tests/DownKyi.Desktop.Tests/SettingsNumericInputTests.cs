using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using DownKyi.Application.Desktop;
using DownKyi.Application.Settings;
using DownKyi.Infrastructure.FFmpeg;
using DownKyi.Infrastructure.Settings;
using DownKyi.Services.Settings;
using DownKyi.ViewModels.Settings;
using DownKyi.ViewModels.Toolbox;
using DownKyi.Views.Settings;
using DownKyi.Views.Toolbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Desktop.Tests;

public sealed class SettingsNumericInputTests
{
    private static readonly string OversizedNumber = new('9', 100);

    [AvaloniaFact]
    public void NetworkNumericInputsNormalizeWithoutExposingBindingExceptions()
    {
        DesktopTestResources.EnsureProductThemeResources();
        var coordinator = new NetworkCoordinatorStub();
        using var viewModel = new NetworkViewModel(
            new InteractionStub(),
            coordinator,
            NullLogger<NetworkViewModel>.Instance);
        viewModel.OnNavigatedTo(Context(AppRoute.SettingsNetwork));
        viewModel.IsHttpProxy = true;
        var view = new BuiltinDownloaderSettingsView { DataContext = viewModel };
        var window = new Window { Content = view, Width = 760, Height = 620 };

        try
        {
            window.Show();
            Settle(window);
            var overall = view.FindControl<TextBox>("NameMaxOverallDownloadLimit")!;
            var task = view.FindControl<TextBox>("NameMaxDownloadLimit")!;
            var port = view.FindControl<TextBox>("NameHttpProxyPort")!;

            overall.Text = $"-{OversizedNumber}";
            task.Text = OversizedNumber;
            port.Text = OversizedNumber;
            Settle(window);
            AssertNoInternalValidationError(view, overall, task, port);

            PressEnter(window, overall);
            PressEnter(window, task);
            PressEnter(window, port);

            Assert.Equal("0", overall.Text);
            Assert.Equal(int.MaxValue.ToString(CultureInfo.InvariantCulture), task.Text);
            Assert.Equal("0", port.Text);
            Assert.Equal(0, coordinator.Current.MaxOverallDownloadLimit);
            Assert.Equal(int.MaxValue, coordinator.Current.MaxDownloadLimit);
            Assert.Equal(0, coordinator.Current.HttpProxyListenPort);
            AssertNoInternalValidationError(view, overall, task, port);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void DanmakuNumericInputsRestoreValidatedValuesWithoutExposingBindingExceptions()
    {
        DesktopTestResources.EnsureProductThemeResources();
        var settingsPath = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-settings-numeric-{Guid.NewGuid():N}.json");
        try
        {
            using var settings = new SettingsStore(settingsPath);
            using var viewModel = new DanmakuViewModel(new InteractionStub(), settings);
            viewModel.OnNavigatedTo(Context(AppRoute.SettingsDanmaku));
            var view = new ViewDanmaku { DataContext = viewModel };
            var window = new Window { Content = view, Width = 840, Height = 900 };

            try
            {
                window.Show();
                Settle(window);
                var width = view.FindControl<TextBox>("NameScreenWidth")!;
                var height = view.FindControl<TextBox>("NameScreenHeight")!;
                var fontSize = view.FindControl<TextBox>("NameFontSize")!;
                var lineCount = view.FindControl<TextBox>("NameLineCount")!;

                width.Text = OversizedNumber;
                height.Text = $"-{OversizedNumber}";
                fontSize.Text = "not-a-number";
                lineCount.Text = OversizedNumber;
                Settle(window);
                AssertNoInternalValidationError(view, width, height, fontSize, lineCount);

                PressEnter(window, width);
                PressEnter(window, height);
                PressEnter(window, fontSize);
                PressEnter(window, lineCount);

                Assert.Equal(settings.Current.Danmaku.ScreenWidth.ToString(CultureInfo.InvariantCulture), width.Text);
                Assert.Equal(settings.Current.Danmaku.ScreenHeight.ToString(CultureInfo.InvariantCulture), height.Text);
                Assert.Equal(settings.Current.Danmaku.FontSize.ToString(CultureInfo.InvariantCulture), fontSize.Text);
                Assert.Equal(settings.Current.Danmaku.LineCount.ToString(CultureInfo.InvariantCulture), lineCount.Text);
                AssertNoInternalValidationError(view, width, height, fontSize, lineCount);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            File.Delete(settingsPath);
        }
    }

    [AvaloniaFact]
    public void DelogoNumericInputsRestoreValidValuesWithoutExposingBindingExceptions()
    {
        DesktopTestResources.EnsureProductThemeResources();
        var settingsPath = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-delogo-numeric-{Guid.NewGuid():N}.json");
        try
        {
            using var settings = new SettingsStore(settingsPath);
            using var viewModel = new DelogoViewModel(
                new InteractionStub(),
                new FilePickerServiceStub(),
                new FfmpegProcessor(settings, NullLoggerFactory.Instance),
                NullLogger<DelogoViewModel>.Instance);
            var view = new ViewDelogo { DataContext = viewModel };
            var window = new Window { Content = view, Width = 1100, Height = 780 };

            try
            {
                window.Show();
                Settle(window);
                var width = view.FindControl<TextBox>("NameLogoWidth")!;
                var height = view.FindControl<TextBox>("NameLogoHeight")!;
                var x = view.FindControl<TextBox>("NameLogoX")!;
                var y = view.FindControl<TextBox>("NameLogoY")!;

                width.Text = $"-{OversizedNumber}";
                height.Text = OversizedNumber;
                x.Text = "-1";
                y.Text = "not-a-number";
                Settle(window);
                AssertNoInternalValidationError(view, width, height, x, y);

                PressEnter(window, width);
                PressEnter(window, height);
                PressEnter(window, x);
                PressEnter(window, y);

                Assert.Equal("100", width.Text);
                Assert.Equal("100", height.Text);
                Assert.Equal("20", x.Text);
                Assert.Equal("20", y.Text);
                AssertNoInternalValidationError(view, width, height, x, y);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            File.Delete(settingsPath);
        }
    }

    private static AppNavigationContext Context(AppRoute route) => new(
        AppNavigationRegion.Settings,
        route,
        AppRoute.Settings,
        null,
        new AppNavigationParameters());

    private static void PressEnter(Window window, TextBox textBox)
    {
        Assert.True(textBox.Focus());
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Settle(window);
    }

    private static void Settle(Window window)
    {
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void AssertNoInternalValidationError(Control view, params TextBox[] inputs)
    {
        foreach (var input in inputs)
        {
            Assert.False(DataValidationErrors.GetHasErrors(input));
        }

        Assert.DoesNotContain(
            view.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text?.Contains("System.", StringComparison.Ordinal) == true
                    || text.Text?.Contains("Exception", StringComparison.Ordinal) == true);
    }

    private sealed class InteractionStub : IDesktopInteractionContext
    {
        public IUserNotificationService Notifications { get; } = new NotificationStub();
        public IAppNavigationService Navigation => null!;
        public IAppDialogService Dialogs => null!;
    }

    private sealed class NotificationStub : IUserNotificationService
    {
        public event EventHandler<UserNotificationEventArgs>? NotificationRaised;

        public void Show(string message)
        {
            NotificationRaised?.Invoke(this, new UserNotificationEventArgs(message));
        }
    }

    private sealed class NetworkCoordinatorStub : INetworkSettingsCoordinator
    {
        public NetworkSettingsOptions Options => NetworkSettingsOptions.Default;

        public NetworkApplicationSettings Current { get; private set; } = new(
            AllowStatus.No,
            string.Empty,
            AllowStatus.No,
            NetworkProxy.None,
            string.Empty,
            3,
            8,
            0,
            0,
            AllowStatus.No,
            string.Empty,
            0);

        public bool Apply(
            Func<NetworkApplicationSettings, NetworkApplicationSettings> update,
            Func<NetworkApplicationSettings, bool> isApplied,
            bool showFeedback)
        {
            Current = update(Current);
            return isApplied(Current);
        }

        public Task<bool> ApplyWithRestartPromptAsync(
            Func<NetworkApplicationSettings, NetworkApplicationSettings> update,
            Func<NetworkApplicationSettings, bool> isApplied,
            bool showFeedback,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Apply(update, isApplied, showFeedback));
        }
    }

    private sealed class FilePickerServiceStub : IFilePickerService
    {
        public Task<string?> SelectFolderAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<string?> SelectVideoAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> SelectVideosAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }
}
