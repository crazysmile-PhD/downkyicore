using DownKyi.Application.Desktop;
using DownKyi.Application.Diagnostics;
using DownKyi.Core.Aria2cNet.Server;
using DownKyi.ViewModels.Dialogs;
using Microsoft.Data.Sqlite;
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
        Assert.Equal("app_diagnostic_report.yml", GetQueryValue(launcher.Uri!, "template"));
        Assert.Equal(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
            launcher.FolderPath?.TrimEnd(Path.DirectorySeparatorChar));
        var issueBody = GetQueryValue(launcher.Uri!, "error_details");
        Assert.Contains("Safe diagnostic summary", issueBody, StringComparison.Ordinal);
        Assert.Contains("Exception type: System.InvalidOperationException", issueBody,
            StringComparison.Ordinal);
        Assert.DoesNotContain("secret-path", issueBody, StringComparison.Ordinal);
        Assert.DoesNotContain("[redacted]", issueBody, StringComparison.Ordinal);
        Assert.Single(notifications.Messages);
    }

    [Fact]
    public void DialogPreservesActionablePackagedAria2RecoveryGuidance()
    {
        var executablePath = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-missing-aria2-{Guid.NewGuid():N}",
            "aria2c.exe");
        var failure = Assert.Throws<FileNotFoundException>(
            () => AriaBinaryIntegrityVerifier.Verify(executablePath));
        var viewModel = new DownloadRuntimeFailureDialogViewModel(
            new RedactingLogService(),
            new RecordingClipboardService(),
            new RecordingPlatformLauncher(),
            new RecordingNotificationService(),
            NullLogger<DownloadRuntimeFailureDialogViewModel>.Instance);

        viewModel.OnDialogOpened(new AppDialogRequest(
            AppDialog.DownloadRuntimeFailure,
            new Dictionary<string, object?> { ["failure"] = failure }));

        Assert.Contains("Redownload and reinstall DownKyi", viewModel.DiagnosticText,
            StringComparison.Ordinal);
        Assert.Contains("If you use the portable archive", viewModel.DiagnosticText,
            StringComparison.Ordinal);
        Assert.Contains("'aria2/aria2c.exe'", viewModel.DiagnosticText,
            StringComparison.Ordinal);
        Assert.DoesNotContain(executablePath, viewModel.DiagnosticText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NewIssueDoesNotTransmitAnUnboundedExceptionMessage()
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

        var body = GetQueryValue(launcher.Uri!, "error_details");
        Assert.True(launcher.Uri!.AbsoluteUri.Length <= 8_000);
        Assert.StartsWith("Safe diagnostic summary", body, StringComparison.Ordinal);
        Assert.Contains("Exception type: System.InvalidOperationException", body,
            StringComparison.Ordinal);
        Assert.DoesNotContain(new string('\u4E0B', 10_000), body, StringComparison.Ordinal);
        Assert.DoesNotContain("truncated", body, StringComparison.Ordinal);
        Assert.True(body.Length < viewModel.DiagnosticText.Length);
    }

    [Theory]
    [InlineData(
        "https://downloads.example.test/file?signature=unrecognized-secret",
        "unrecognized-secret")]
    [InlineData("/srv/alice/downkyi/runtime.db", "alice/downkyi")]
    [InlineData("Database:/srv/alice/downkyi/runtime.db", "alice/downkyi")]
    [InlineData("/srv/Jane Doe/downkyi/runtime.db", "Doe/downkyi")]
    [InlineData("Database:/srv/Jane Doe/downkyi/runtime.db", "Doe/downkyi")]
    [InlineData(@"C:\Users\Jane Doe\downkyi\runtime.db", @"Jane Doe\downkyi")]
    [InlineData(@"Database:C:\Users\Jane Doe\downkyi\runtime.db", @"Jane Doe\downkyi")]
    [InlineData(@"\\server\Jane Doe\downkyi\runtime.db", @"Jane Doe\downkyi")]
    public async Task DialogRedactsExternalResourcesBeforeDisplayCopyAndIssue(
        string resource,
        string sensitiveFragment)
    {
        var clipboard = new RecordingClipboardService();
        var launcher = new RecordingPlatformLauncher();
        var viewModel = new DownloadRuntimeFailureDialogViewModel(
            new RedactingLogService(),
            clipboard,
            launcher,
            new RecordingNotificationService(),
            NullLogger<DownloadRuntimeFailureDialogViewModel>.Instance);
        viewModel.OnDialogOpened(new AppDialogRequest(
            AppDialog.DownloadRuntimeFailure,
            new Dictionary<string, object?>
            {
                ["failure"] = new InvalidOperationException($"Request failed for {resource}")
            }));

        await viewModel.CopyErrorDetailsAsync();
        await viewModel.CreateGitHubIssueAsync();

        Assert.Contains("[resource redacted]", viewModel.DiagnosticText, StringComparison.Ordinal);
        Assert.DoesNotContain(resource, viewModel.DiagnosticText, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveFragment, viewModel.DiagnosticText, StringComparison.Ordinal);
        Assert.Equal(viewModel.DiagnosticText, clipboard.Text);
        var body = GetQueryValue(launcher.Uri!, "error_details");
        Assert.Contains("Exception type: System.InvalidOperationException", body,
            StringComparison.Ordinal);
        Assert.DoesNotContain(resource, body, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveFragment, body, StringComparison.Ordinal);
        Assert.DoesNotContain("Request failed for", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IssueOmitsUnrecognizedPrivateValuesFromExceptionMessages()
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
                ["failure"] = CreatePrivateFailure()
            }));

        await viewModel.CreateGitHubIssueAsync();

        Assert.Contains("opaque-private-value-73918", viewModel.DiagnosticText,
            StringComparison.Ordinal);
        var body = GetQueryValue(launcher.Uri!, "error_details");
        Assert.Contains("at DownKyi.Tests.DownloadRuntimeFailureDialogViewModelTests.",
            body, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadRuntimeFailureDialogViewModelTests.cs", body,
            StringComparison.Ordinal);
        Assert.DoesNotContain("opaque-private-value-73918", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IssueIncludesSqliteCodesWithoutTheSqliteMessage()
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
                ["failure"] = new SqliteException("private-sqlite-message-73918", 19)
            }));

        await viewModel.CreateGitHubIssueAsync();

        var body = GetQueryValue(launcher.Uri!, "error_details");
        Assert.Contains("SQLite error code: 19", body, StringComparison.Ordinal);
        Assert.Contains("SQLite extended error code: 19", body, StringComparison.Ordinal);
        Assert.DoesNotContain("private-sqlite-message-73918", body, StringComparison.Ordinal);
    }

    [Fact]
    public void DialogHandlesLargeDotSeparatedNonResourceDiagnostic()
    {
        var viewModel = new DownloadRuntimeFailureDialogViewModel(
            new RedactingLogService(),
            new RecordingClipboardService(),
            new RecordingPlatformLauncher(),
            new RecordingNotificationService(),
            NullLogger<DownloadRuntimeFailureDialogViewModel>.Instance);
        var diagnostic = string.Join('.', Enumerable.Repeat("segment", 20_000));

        viewModel.OnDialogOpened(new AppDialogRequest(
            AppDialog.DownloadRuntimeFailure,
            new Dictionary<string, object?>
            {
                ["failure"] = new InvalidOperationException(diagnostic)
            }));

        Assert.DoesNotContain(
            "[resource redacted]",
            viewModel.DiagnosticText,
            StringComparison.Ordinal);
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

    private static InvalidOperationException CreatePrivateFailure()
    {
        try
        {
            throw new InvalidOperationException("opaque-private-value-73918");
        }
        catch (InvalidOperationException exception)
        {
            return exception;
        }
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

        public Task<string> ExportFeedbackPackageAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Path.Combine(Path.GetTempPath(), "downkyi-feedback.zip"));
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

        public string? FolderPath { get; private set; }

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
            CancellationToken cancellationToken = default)
        {
            FolderPath = path;
            return Task.FromResult(true);
        }
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
