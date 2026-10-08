using DownKyi.Application.Desktop;
using DownKyi.Application.Diagnostics;
using DownKyi.Services;
using DownKyi.ViewModels.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class ViewAboutFeedbackTests
{
    [Fact]
    public async Task FeedbackExportsPackageAndOpensRequiredUploadForm()
    {
        var logs = new RecordingLogService(
        [
            CreateRecord(LogLevel.Information, "informational context"),
            CreateRecord(
                LogLevel.Warning,
                "signed media https://media.example.test/video.m4s?signature=private-signature"),
            CreateRecord(
                LogLevel.Error,
                "request failed with secret-token",
                "InvalidOperationException: callback https://callback.example.test/user/42")
        ]);
        var launcher = new RecordingPlatformLauncher();
        using var settings = new TestSettingsStore();
        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://api.github.test/")
        };
        using var viewModel = new ViewAboutViewModel(
            new TestDesktopInteractionContext(),
            settings.Store,
            logs,
            launcher,
            new VersionCheckerService(httpClient, "owner", "repo"),
            NullLogger<ViewAboutViewModel>.Instance);

        await viewModel.ExecuteFeedbackCommand();

        Assert.Equal("github.com", launcher.Uri?.Host);
        Assert.Equal("/crazysmile-PhD/downkyicore/issues/new", launcher.Uri?.AbsolutePath);
        Assert.Equal("[Bug] 用户反馈", GetQueryValue(launcher.Uri!, "title"));
        Assert.Equal("app_diagnostic_report.yml", GetQueryValue(launcher.Uri!, "template"));
        Assert.Equal(viewModel.AppVersion, GetQueryValue(launcher.Uri!, "app_version"));
        Assert.DoesNotContain("body=", launcher.Uri!.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("private-signature", launcher.Uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", launcher.Uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
            launcher.FolderPath?.TrimEnd(Path.DirectorySeparatorChar));
        Assert.True(logs.ExportCalled);
    }

    [Fact]
    public async Task FeedbackTruncatesOnlyPrefilledVersionWhenItExceedsSafeUriLength()
    {
        var logs = new RecordingLogService(
        [
            CreateRecord(LogLevel.Error, new string('错', 10_000))
        ]);
        var launcher = new RecordingPlatformLauncher();
        using var settings = new TestSettingsStore();
        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://api.github.test/")
        };
        using var viewModel = new ViewAboutViewModel(
            new TestDesktopInteractionContext(),
            settings.Store,
            logs,
            launcher,
            new VersionCheckerService(httpClient, "owner", "repo"),
            NullLogger<ViewAboutViewModel>.Instance);
        viewModel.AppVersion = new string('错', 10_000);

        await viewModel.ExecuteFeedbackCommand();

        Assert.True(launcher.Uri!.AbsoluteUri.Length <= 8_000);
        Assert.Equal("app_diagnostic_report.yml", GetQueryValue(launcher.Uri, "template"));
        var version = GetQueryValue(launcher.Uri, "app_version");
        Assert.StartsWith("错", version, StringComparison.Ordinal);
        Assert.EndsWith(
            "[预填信息已截断；请上传生成的诊断 ZIP。]",
            version,
            StringComparison.Ordinal);
    }

    private static ApplicationLogRecord CreateRecord(
        LogLevel level,
        string message,
        string exceptionText = "")
    {
        return new ApplicationLogRecord(
            new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero),
            level,
            "Feedback.Tests",
            new EventId(17, "Feedback"),
            message,
            string.IsNullOrEmpty(exceptionText) ? string.Empty : "InvalidOperationException",
            42,
            7,
            string.Empty,
            exceptionText);
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

    private sealed class RecordingLogService(
        IReadOnlyList<ApplicationLogRecord> records) : IApplicationLogService
    {
        public string LogDirectory => string.Empty;

        public bool ExportCalled { get; private set; }

        public IReadOnlyList<ApplicationLogRecord> GetRecentEvents() => records;

        public ApplicationLogMetrics GetMetrics() =>
            new(0, 0, 0, 0, 0, 0, 0, 0, null);

        public string RedactDiagnosticText(string? text) =>
            (text ?? string.Empty).Replace(
                "secret-token",
                "[redacted]",
                StringComparison.Ordinal);

        public Task FlushAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<string> ExportDiagnosticLogAsync(
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<string> ExportFeedbackPackageAsync(
            CancellationToken cancellationToken = default)
        {
            ExportCalled = true;
            return Task.FromResult(Path.Combine(Path.GetTempPath(), "downkyi-feedback.zip"));
        }
    }

    private sealed class RecordingPlatformLauncher : IPlatformLauncher
    {
        public Uri? Uri { get; private set; }

        public string? FolderPath { get; private set; }

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

        public Task<bool> OpenUriAsync(
            Uri uri,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Uri = uri;
            return Task.FromResult(true);
        }
    }
}
