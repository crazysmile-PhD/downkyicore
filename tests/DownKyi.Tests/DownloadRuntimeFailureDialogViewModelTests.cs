using DownKyi.Application.Desktop;
using DownKyi.Application.Diagnostics;
using DownKyi.ViewModels.Dialogs;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class DownloadRuntimeFailureDialogViewModelTests
{
    [Fact]
    public async Task DialogCopiesRedactedDiagnosticAndOpensNewIssuePage()
    {
        var logs = new RedactingLogService();
        var clipboard = new RecordingClipboardService();
        var launcher = new RecordingPlatformLauncher();
        var notifications = new RecordingNotificationService();
        var viewModel = new DownloadRuntimeFailureDialogViewModel(
            logs,
            clipboard,
            launcher,
            notifications,
            NullLogger<DownloadRuntimeFailureDialogViewModel>.Instance);
        viewModel.OnDialogOpened(new AppDialogRequest(
            AppDialog.DownloadRuntimeFailure,
            new Dictionary<string, object?>
            {
                ["failure"] = new InvalidOperationException("secret-path")
            }));

        await viewModel.CopyErrorDetailsAsync();
        await viewModel.CreateGitHubIssueAsync();

        Assert.Equal(viewModel.DiagnosticText, clipboard.Text);
        Assert.Contains("Download bootstrap", viewModel.DiagnosticText, StringComparison.Ordinal);
        Assert.Contains("[redacted]", viewModel.DiagnosticText, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-path", viewModel.DiagnosticText, StringComparison.Ordinal);
        Assert.Equal("github.com", launcher.Uri?.Host);
        Assert.Equal("/crazysmile-PhD/downkyicore/issues/new", launcher.Uri?.AbsolutePath);
        Assert.Contains("title=", launcher.Uri?.Query, StringComparison.Ordinal);
        Assert.Single(notifications.Messages);
    }

    private sealed class RedactingLogService : IApplicationLogService
    {
        public string LogDirectory => string.Empty;

        public IReadOnlyList<ApplicationLogRecord> GetRecentEvents() => [];

        public ApplicationLogMetrics GetMetrics() =>
            new(0, 0, 0, 0, 0, 0, 0, 0, null);

        public string RedactDiagnosticText(string? text) =>
            (text ?? string.Empty).Replace("secret-path", "[redacted]", StringComparison.Ordinal);

        public Task FlushAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<string> ExportDiagnosticLogAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingClipboardService : IClipboardService
    {
        public string? Text { get; private set; }

        public Task SetTextAsync(
            string text,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Text = text;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingPlatformLauncher : IPlatformLauncher
    {
        public Uri? Uri { get; private set; }

        public Task<bool> OpenUriAsync(
            Uri uri,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Uri = uri;
            return Task.FromResult(true);
        }

        public Task<bool> OpenFileAsync(
            string path,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> OpenFolderAsync(
            string path,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingNotificationService : IUserNotificationService
    {
        public event EventHandler<UserNotificationEventArgs>? NotificationRaised
        {
            add { }
            remove { }
        }

        public List<string> Messages { get; } = [];

        public void Show(string message)
        {
            Messages.Add(message);
        }
    }
}
