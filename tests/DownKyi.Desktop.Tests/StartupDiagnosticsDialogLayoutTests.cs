using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DownKyi.Application.Desktop;
using DownKyi.Application.Diagnostics;
using DownKyi.Application.Lifetime;
using DownKyi.Infrastructure.Logging;
using DownKyi.Platform;
using DownKyi.Services.Migration;
using DownKyi.Services.Startup;
using DownKyi.ViewModels.Dialogs;
using DownKyi.Views.Dialogs;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Desktop.Tests;

public sealed class StartupDiagnosticsDialogLayoutTests
{
    [AvaloniaFact]
    public void LongExpandedDiagnosticsScrollWhileFooterRemainsVisible()
    {
        var application = DesktopTestResources.EnsureDownloadProjectionResources();
        DesktopTestResources.EnsureProductThemeResources();
        var fluentTheme = new FluentTheme();
        application.Styles.Insert(0, fluentTheme);
        var viewModel = CreateViewModel();
        var longLine = string.Concat(Enumerable.Repeat("very-long-diagnostic-segment", 2_000));
        viewModel.OnDialogOpened(CreateRequest(
            StartupDiagnostic.ForDownloadBootstrap(
                new InvalidOperationException(longLine))));
        Assert.Equal("启动时发现 1 项问题", viewModel.Title);
        Assert.Single(viewModel.Items);

        viewModel.OnDialogOpened(CreateRequest(
            StartupDiagnostic.ForDownloadBootstrap(
                new InvalidOperationException(longLine)),
            StartupDiagnostic.ForLegacyMigration(
                new InvalidOperationException(
                    string.Join(Environment.NewLine, Enumerable.Repeat("stack frame", 400))))));
        Assert.Equal("启动时发现 2 项问题", viewModel.Title);
        var view = new StartupDiagnosticsDialog { DataContext = viewModel };
        var window = new Window
        {
            Content = view,
            Width = 520,
            Height = 420
        };

        try
        {
            window.Show();
            window.UpdateLayout();

            var issuesScroll = Assert.IsType<ScrollViewer>(
                view.FindControl<ScrollViewer>("IssuesScrollViewer"));
            var footer = Assert.IsType<Border>(view.FindControl<Border>("FooterActions"));
            AssertFooterFits(view, footer);
            Assert.All(viewModel.Items, item => Assert.False(item.IsExpanded));

            viewModel.Items[0].ToggleDetailsCommand.Execute(null);
            window.UpdateLayout();

            Assert.True(viewModel.Items[0].IsExpanded);
            Assert.False(viewModel.Items[1].IsExpanded);
            var diagnosticBox = Assert.Single(
                view.GetLogicalDescendants().OfType<TextBox>(),
                textBox => textBox.IsVisible && textBox.Text == viewModel.Items[0].DiagnosticText);
            Assert.Equal(TextWrapping.Wrap, diagnosticBox.TextWrapping);
            Assert.Equal(
                ScrollBarVisibility.Disabled,
                ScrollViewer.GetHorizontalScrollBarVisibility(diagnosticBox));
            Assert.Equal(
                ScrollBarVisibility.Auto,
                ScrollViewer.GetVerticalScrollBarVisibility(diagnosticBox));
            Assert.True(diagnosticBox.Bounds.Width <= issuesScroll.Viewport.Width + 0.5);

            viewModel.Items[1].ToggleDetailsCommand.Execute(null);
            window.UpdateLayout();

            Assert.True(viewModel.Items[0].IsExpanded);
            Assert.True(viewModel.Items[1].IsExpanded);
            Assert.True(issuesScroll.Extent.Height > issuesScroll.Viewport.Height);
            AssertFooterFits(view, footer);
            foreach (var size in new[] { new Size(360, 260), new Size(480, 360), new Size(760, 680) })
            {
                window.Width = size.Width;
                window.Height = size.Height;
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Assert.Equal(size, view.Bounds.Size);
                Assert.True(issuesScroll.Extent.Height > issuesScroll.Viewport.Height);
                Assert.True(issuesScroll.Extent.Width <= issuesScroll.Viewport.Width + 0.5);
                AssertFooterFits(view, footer);
            }
        }
        finally
        {
            window.Close();
            application.Styles.Remove(fluentTheme);
        }
    }

    [Fact]
    public void StartupDiagnosticSizeUsesLogicalWorkingAreaAtHighDpi()
    {
        var size = AvaloniaDialogService.ConstrainDialogSize(
            AppDialog.StartupDiagnostics,
            new PixelRect(0, 0, 1280, 720),
            scaling: 2,
            ownerSize: new Size(800, 550));

        Assert.Equal(new Size(608, 328), size);
    }

    [AvaloniaFact]
    public async Task FailedMigrationDoesNotLeaveLoadingAnimationActive()
    {
        var application = DesktopTestResources.EnsureDownloadProjectionResources();
        DesktopTestResources.EnsureProductThemeResources();
        var fluentTheme = new FluentTheme();
        application.Styles.Insert(0, fluentTheme);
        var failure = new InvalidOperationException("migration failed");
        var viewModel = new UpgradingDialogViewModel(
            new FailedMigrationCoordinator(failure),
            new ApplicationLifecycleStub(),
            NullLogger<UpgradingDialogViewModel>.Instance);
        await using var viewModelScope = viewModel.ConfigureAwait(true);
        viewModel.OnDialogOpened(new AppDialogRequest(AppDialog.LegacyUpgrade));
        var view = new ViewUpgradingDialog { DataContext = viewModel };
        var window = new Window { Content = view };

        try
        {
            window.Show();
            window.UpdateLayout();

            var loading = Assert.Single(
                view.GetVisualDescendants().OfType<DownKyi.CustomControl.Loading>());
            Assert.False(viewModel.IsMigrationActive);
            Assert.False(loading.IsActive);
        }
        finally
        {
            window.Close();
            application.Styles.Remove(fluentTheme);
        }
    }

    private static void AssertFooterFits(Control view, Control footer)
    {
        var footerOrigin = footer.TranslatePoint(default, view);
        Assert.NotNull(footerOrigin);
        Assert.InRange(footerOrigin.Value.X, 0, view.Bounds.Width);
        Assert.InRange(footerOrigin.Value.Y, 0, view.Bounds.Height);
        Assert.True(footerOrigin.Value.X + footer.Bounds.Width <= view.Bounds.Width + 0.5);
        Assert.True(footerOrigin.Value.Y + footer.Bounds.Height <= view.Bounds.Height + 0.5);
        Assert.All(
            footer.GetVisualDescendants().OfType<Button>(),
            button =>
            {
                var buttonOrigin = button.TranslatePoint(default, footer);
                Assert.NotNull(buttonOrigin);
                Assert.True(buttonOrigin.Value.Y + button.Bounds.Height <= footer.Bounds.Height + 0.5);
            });
    }

    private static StartupDiagnosticsDialogViewModel CreateViewModel()
    {
        return new StartupDiagnosticsDialogViewModel(
            new LogServiceStub(),
            new ClipboardServiceStub(),
            new PlatformLauncherStub(),
            new NotificationServiceStub(),
            NullLogger<StartupDiagnosticsDialogViewModel>.Instance);
    }

    private static AppDialogRequest CreateRequest(params StartupDiagnostic[] diagnostics)
    {
        return new AppDialogRequest(
            AppDialog.StartupDiagnostics,
            new Dictionary<string, object?>
            {
                [StartupDiagnosticsDialogViewModel.DiagnosticsParameter] = diagnostics
            });
    }

    private sealed class LogServiceStub : IApplicationLogService
    {
        public string LogDirectory => string.Empty;

        public IReadOnlyList<ApplicationLogRecord> GetRecentEvents() => [];

        public ApplicationLogMetrics GetMetrics() => new(0, 0, 0, 0, 0, 0, 0, 0, null);

        public string RedactDiagnosticText(string? text) => text ?? string.Empty;

        public Task FlushAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<string> ExportDiagnosticLogAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<string> ExportFeedbackPackageAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Path.Combine(Path.GetTempPath(), "downkyi-feedback.zip"));
    }

    private sealed class ClipboardServiceStub : IClipboardService
    {
        public Task SetTextAsync(string text, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class PlatformLauncherStub : IPlatformLauncher
    {
        public Task<bool> OpenFileAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> OpenFolderAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> OpenUriAsync(Uri uri, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class NotificationServiceStub : IUserNotificationService
    {
        public event EventHandler<UserNotificationEventArgs>? NotificationRaised
        {
            add { }
            remove { }
        }

        public void Show(string message)
        {
        }
    }

    private sealed class FailedMigrationCoordinator(Exception failure) : ILegacyUpgradeCoordinator
    {
        public bool IsUpgradeRequired() => true;

        public Task<LegacyUpgradeResult> UpgradeAsync(
            IProgress<LegacyUpgradeProgress> progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new LegacyUpgradeResult(
                LegacyUpgradeOutcome.Failed,
                "migration failed",
                failure));
        }
    }

    private sealed class ApplicationLifecycleStub : IApplicationLifecycle
    {
        public Task RequestShutdownAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ExitAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> RestartAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
