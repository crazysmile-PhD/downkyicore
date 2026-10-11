using DownKyi.Application.Desktop;
using DownKyi.Application.Diagnostics;
using DownKyi.Core.Aria2cNet.Server;
using DownKyi.Infrastructure.Downloads;
using DownKyi.Services.Startup;
using DownKyi.ViewModels.Dialogs;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class StartupDiagnosticsDialogViewModelTests
{
    [Fact]
    public void DialogUsesActualIssueCountAndKeepsItemsSeparate()
    {
        var viewModel = new StartupDiagnosticsDialogViewModel(
            new RedactingLogService(),
            new RecordingClipboardService(),
            new RecordingPlatformLauncher(),
            new RecordingNotificationService(),
            NullLogger<StartupDiagnosticsDialogViewModel>.Instance);

        viewModel.OnDialogOpened(CreateRequest(
            StartupDiagnostic.ForDownloadBootstrap(
                new InvalidOperationException("download failed"))));

        Assert.Single(viewModel.Items);

        viewModel.OnDialogOpened(CreateRequest(
            StartupDiagnostic.ForDownloadBootstrap(
                new InvalidOperationException("download failed")),
            StartupDiagnostic.ForLegacyMigration(
                new InvalidOperationException("migration failed"))));

        Assert.Collection(
            viewModel.Items,
            item => Assert.Equal(
                StartupDiagnosticKind.DownloadRuntimeFailed,
                item.Diagnostic.Kind),
            item => Assert.Equal(
                StartupDiagnosticKind.LegacyMigrationFailed,
                item.Diagnostic.Kind));
    }

    [Fact]
    public async Task ResetActionIsAvailableOnlyForDownloadSchemaMismatch()
    {
        var viewModel = new StartupDiagnosticsDialogViewModel(
            new RedactingLogService(),
            new RecordingClipboardService(),
            new RecordingPlatformLauncher(),
            new RecordingNotificationService(),
            NullLogger<StartupDiagnosticsDialogViewModel>.Instance);

        var schemaFailure = new InvalidOperationException(
            "bootstrap wrapper",
            new DownloadStoreSchemaMismatchException("unsupported schema"));
        viewModel.OnDialogOpened(CreateRequest(
            StartupDiagnostic.ForDownloadBootstrap(schemaFailure)));

        var schemaItem = Assert.Single(viewModel.Items);
        Assert.True(schemaItem.CanResetDownloadStore);
        Assert.Equal(StartupDiagnosticKind.DownloadStoreSchemaMismatch, schemaItem.Diagnostic.Kind);

        var closeCompletion = new TaskCompletionSource<AppDialogResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.CloseRequested += (_, closeResult) =>
            closeCompletion.TrySetResult(closeResult);
        schemaItem.ResetDownloadStoreCommand.Execute(null);
        var result = await closeCompletion.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AppDialogOutcome.Accepted, result.Outcome);
        Assert.Same(
            schemaFailure,
            Assert.IsType<StartupDiagnostic>(
                result.Parameters[StartupDiagnosticsDialogViewModel.RecoveryDiagnosticParameter])
                .Failure);

        viewModel.OnDialogOpened(CreateRequest(
            StartupDiagnostic.ForDownloadBootstrap(
                new SqliteException("database is locked", 5))));

        Assert.False(Assert.Single(viewModel.Items).CanResetDownloadStore);
    }

    [Fact]
    public async Task BuiltInFallbackActionIsAvailableOnlyWhenPackagedAriaIsMissing()
    {
        var viewModel = new StartupDiagnosticsDialogViewModel(
            new RedactingLogService(),
            new RecordingClipboardService(),
            new RecordingPlatformLauncher(),
            new RecordingNotificationService(),
            NullLogger<StartupDiagnosticsDialogViewModel>.Instance);
        var missingRuntime = StartupDiagnostic.ForDownloadBootstrap(
            new FileNotFoundException("aria2 executable missing"));
        viewModel.OnDialogOpened(CreateRequest(missingRuntime));

        var missingRuntimeItem = Assert.Single(viewModel.Items);
        Assert.True(missingRuntimeItem.CanUseBuiltInDownloader);
        Assert.False(missingRuntimeItem.CanResetDownloadStore);

        var closeCompletion = new TaskCompletionSource<AppDialogResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.CloseRequested += (_, closeResult) =>
            closeCompletion.TrySetResult(closeResult);
        missingRuntimeItem.UseBuiltInDownloaderCommand.Execute(null);

        var result = await closeCompletion.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AppDialogOutcome.Accepted, result.Outcome);
        Assert.Same(
            missingRuntime,
            result.Parameters[
                StartupDiagnosticsDialogViewModel.BuiltInFallbackDiagnosticParameter]);

        viewModel.OnDialogOpened(CreateRequest(
            StartupDiagnostic.ForDownloadBootstrap(
                new InvalidOperationException("aria2 startup failed"))));

        Assert.False(Assert.Single(viewModel.Items).CanUseBuiltInDownloader);
    }

    [Fact]
    public async Task DialogCopiesRedactedDiagnosticAndPrefillsNewIssue()
    {
        var logs = new RedactingLogService();
        var clipboard = new RecordingClipboardService();
        var launcher = new RecordingPlatformLauncher();
        var notifications = new RecordingNotificationService();
        var viewModel = new StartupDiagnosticsDialogViewModel(
            logs,
            clipboard,
            launcher,
            notifications,
            NullLogger<StartupDiagnosticsDialogViewModel>.Instance);
        viewModel.OnDialogOpened(CreateRequest(
            StartupDiagnostic.ForDownloadBootstrap(
                new InvalidOperationException("secret-path"))));

        await viewModel.CopyAllDiagnosticsAsync();
        await viewModel.CreateGitHubIssueAsync();

        Assert.Equal(viewModel.DiagnosticText, clipboard.Text);
        Assert.Contains("DownloadBootstrap", viewModel.DiagnosticText, StringComparison.Ordinal);
        Assert.Contains("[redacted]", viewModel.DiagnosticText, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-path", viewModel.DiagnosticText, StringComparison.Ordinal);
        Assert.Equal("github.com", launcher.Uri?.Host);
        Assert.Equal("/crazysmile-PhD/downkyicore/issues/new", launcher.Uri?.AbsolutePath);
        Assert.Equal("DownKyi startup diagnostics report", GetQueryValue(launcher.Uri!, "title"));
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
        var viewModel = new StartupDiagnosticsDialogViewModel(
            new RedactingLogService(),
            new RecordingClipboardService(),
            new RecordingPlatformLauncher(),
            new RecordingNotificationService(),
            NullLogger<StartupDiagnosticsDialogViewModel>.Instance);

        viewModel.OnDialogOpened(CreateRequest(
            StartupDiagnostic.ForDownloadBootstrap(failure)));

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
        var viewModel = new StartupDiagnosticsDialogViewModel(
            new RedactingLogService(),
            new RecordingClipboardService(),
            launcher,
            new RecordingNotificationService(),
            NullLogger<StartupDiagnosticsDialogViewModel>.Instance);
        viewModel.OnDialogOpened(CreateRequest(
            StartupDiagnostic.ForDownloadBootstrap(
                new InvalidOperationException(new string('\u4E0B', 10_000)))));

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
        var viewModel = new StartupDiagnosticsDialogViewModel(
            new RedactingLogService(),
            clipboard,
            launcher,
            new RecordingNotificationService(),
            NullLogger<StartupDiagnosticsDialogViewModel>.Instance);
        viewModel.OnDialogOpened(CreateRequest(
            StartupDiagnostic.ForDownloadBootstrap(
                new InvalidOperationException($"Request failed for {resource}"))));

        await viewModel.CopyAllDiagnosticsAsync();
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
        var viewModel = new StartupDiagnosticsDialogViewModel(
            new RedactingLogService(),
            new RecordingClipboardService(),
            launcher,
            new RecordingNotificationService(),
            NullLogger<StartupDiagnosticsDialogViewModel>.Instance);
        viewModel.OnDialogOpened(CreateRequest(
            StartupDiagnostic.ForDownloadBootstrap(CreatePrivateFailure())));

        await viewModel.CreateGitHubIssueAsync();

        Assert.Contains("opaque-private-value-73918", viewModel.DiagnosticText,
            StringComparison.Ordinal);
        var body = GetQueryValue(launcher.Uri!, "error_details");
        Assert.Contains("at DownKyi.Tests.StartupDiagnosticsDialogViewModelTests.",
            body, StringComparison.Ordinal);
        Assert.DoesNotContain("StartupDiagnosticsDialogViewModelTests.cs", body,
            StringComparison.Ordinal);
        Assert.DoesNotContain("opaque-private-value-73918", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IssueIncludesSqliteCodesWithoutTheSqliteMessage()
    {
        var launcher = new RecordingPlatformLauncher();
        var viewModel = new StartupDiagnosticsDialogViewModel(
            new RedactingLogService(),
            new RecordingClipboardService(),
            launcher,
            new RecordingNotificationService(),
            NullLogger<StartupDiagnosticsDialogViewModel>.Instance);
        viewModel.OnDialogOpened(CreateRequest(
            StartupDiagnostic.ForDownloadBootstrap(
                new SqliteException("private-sqlite-message-73918", 19))));

        await viewModel.CreateGitHubIssueAsync();

        var body = GetQueryValue(launcher.Uri!, "error_details");
        Assert.Contains("SQLite error code: 19", body, StringComparison.Ordinal);
        Assert.Contains("SQLite extended error code: 19", body, StringComparison.Ordinal);
        Assert.DoesNotContain("private-sqlite-message-73918", body, StringComparison.Ordinal);
    }

    [Fact]
    public void DialogHandlesLargeDotSeparatedNonResourceDiagnostic()
    {
        var viewModel = new StartupDiagnosticsDialogViewModel(
            new RedactingLogService(),
            new RecordingClipboardService(),
            new RecordingPlatformLauncher(),
            new RecordingNotificationService(),
            NullLogger<StartupDiagnosticsDialogViewModel>.Instance);
        var diagnostic = string.Join('.', Enumerable.Repeat("segment", 20_000));

        viewModel.OnDialogOpened(CreateRequest(
            StartupDiagnostic.ForDownloadBootstrap(
                new InvalidOperationException(diagnostic))));

        Assert.DoesNotContain(
            "[resource redacted]",
            viewModel.DiagnosticText,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CopyAllPreservesEveryRedactedDiagnosticAndOriginalLineBreaks()
    {
        var clipboard = new RecordingClipboardService();
        var viewModel = new StartupDiagnosticsDialogViewModel(
            new RedactingLogService(),
            clipboard,
            new RecordingPlatformLauncher(),
            new RecordingNotificationService(),
            NullLogger<StartupDiagnosticsDialogViewModel>.Instance);
        var migrationFailure = new InvalidOperationException(
            "migration line one" + Environment.NewLine + "migration line two");
        var downloadFailure = new InvalidOperationException(
            "download line one" + Environment.NewLine + "secret-path");
        viewModel.OnDialogOpened(CreateRequest(
            StartupDiagnostic.ForLegacyMigration(migrationFailure),
            StartupDiagnostic.ForDownloadBootstrap(downloadFailure)));

        await viewModel.CopyAllDiagnosticsAsync();

        Assert.Equal(viewModel.DiagnosticText, clipboard.Text);
        Assert.Contains(
            "migration line one" + Environment.NewLine + "migration line two",
            clipboard.Text,
            StringComparison.Ordinal);
        Assert.Contains("download line one", clipboard.Text, StringComparison.Ordinal);
        Assert.Contains("[redacted]", clipboard.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-path", clipboard.Text, StringComparison.Ordinal);
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
