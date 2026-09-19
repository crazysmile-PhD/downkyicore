using DownKyi.Application.Desktop;
using DownKyi.Application.Diagnostics;
using DownKyi.ViewModels.Dialogs;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class DownloadRuntimeFailureDialogViewModelTests
{
    [Fact]
    public async Task DialogCopiesRedactedDiagnosticAndPrefillsNewIssue()
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
        Assert.Equal("Download system failed to initialize", GetQueryValue(launcher.Uri!, "title"));
        Assert.Equal(viewModel.DiagnosticText, GetQueryValue(launcher.Uri!, "body"));
        Assert.Single(notifications.Messages);
    }

    [Fact]
    public async Task NewIssueTruncatesOnlyItsBodyWhenDiagnosticWouldExceedSafeUriLength()
    {
        var launcher = new RecordingPlatformLauncher();
        var viewModel = new DownloadRuntimeFailureDialogViewModel(
            new RedactingLogService(),
            new RecordingClipboardService(),
            launcher,
            new RecordingNotificationService(),
            NullLogger<DownloadRuntimeFailureDialogViewModel>.Instance);
        viewModel.OnDialogOpened(new AppDialogRequest(
            AppDialog.DownloadRuntimeFailure,
            new Dictionary<string, object?>
            {
                ["failure"] = new InvalidOperationException(new string('\u4E0B', 10_000))
            }));

        await viewModel.CreateGitHubIssueAsync();

        var body = GetQueryValue(launcher.Uri!, "body");
        Assert.True(launcher.Uri!.AbsoluteUri.Length <= 8_000);
        Assert.StartsWith("DownKyi version:", body, StringComparison.Ordinal);
        Assert.EndsWith(
            "Use Copy Error Details for the complete text.]",
            body,
            StringComparison.Ordinal);
        Assert.True(body.Length < viewModel.DiagnosticText.Length);
    }

    private static string GetQueryValue(Uri uri, string name)
    {
        foreach (var field in uri.Query.TrimStart('?').Split('&'))
        {
            var parts = field.Split('=', 2);
            if (parts.Length == 2 && string.Equals(parts[0], name, StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(parts[1]);
            }
        }

        throw new InvalidOperationException($"Query parameter '{name}' was not found.");
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
