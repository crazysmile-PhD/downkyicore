using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Application.Diagnostics;
using DownKyi.Commands;
using DownKyi.Infrastructure.Logging;
using DownKyi.Models;
using DownKyi.Services;
using DownKyi.Services.Download;
using DownKyi.Utils;
using Microsoft.Extensions.Logging;

namespace DownKyi.ViewModels.Dialogs;

internal sealed class DownloadRuntimeFailureDialogViewModel : BaseDialogViewModel
{
    private const string IssueTitle = "Download system failed to initialize";
    private const string IssueTemplate = "app_diagnostic_report.yml";
    private const string TruncatedDiagnosticSuffix =
        "\n\n[The summary was truncated; attach the generated diagnostic ZIP.]";

    private readonly IApplicationLogService _logService;
    private readonly IClipboardService _clipboardService;
    private readonly IPlatformLauncher _platformLauncher;
    private readonly IUserNotificationService _notifications;
    private readonly ILogger<DownloadRuntimeFailureDialogViewModel> _logger;
    private DownKyiAsyncDelegateCommand? _copyErrorDetailsCommand;
    private DownKyiAsyncDelegateCommand? _createGitHubIssueCommand;
    private RelayCommand? _resetDownloadStoreCommand;
    private string _failureMessage;
    private string _diagnosticText = string.Empty;
    private string _issueDiagnosticText = string.Empty;
    private bool _canResetDownloadStore;

    public DownloadRuntimeFailureDialogViewModel(
        IApplicationLogService logService,
        IClipboardService clipboardService,
        IPlatformLauncher platformLauncher,
        IUserNotificationService notifications,
        ILogger<DownloadRuntimeFailureDialogViewModel> logger)
    {
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _clipboardService = clipboardService
            ?? throw new ArgumentNullException(nameof(clipboardService));
        _platformLauncher = platformLauncher
            ?? throw new ArgumentNullException(nameof(platformLauncher));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Title = DictionaryResource.GetString("DownloadRuntimeFailureTitle");
        _failureMessage = DictionaryResource.GetString("DownloadRuntimeFailureMessage");
    }

    public string FailureMessage
    {
        get => _failureMessage;
        private set => SetProperty(ref _failureMessage, value);
    }

    internal string FailureMessageResourceKey { get; private set; } =
        "DownloadRuntimeFailureMessage";

    public string DiagnosticText
    {
        get => _diagnosticText;
        private set => SetProperty(ref _diagnosticText, value);
    }

    public DownKyiAsyncDelegateCommand CopyErrorDetailsCommand =>
        _copyErrorDetailsCommand ??= new DownKyiAsyncDelegateCommand(
            CopyErrorDetailsAsync,
            _logger);

    public DownKyiAsyncDelegateCommand CreateGitHubIssueCommand =>
        _createGitHubIssueCommand ??= new DownKyiAsyncDelegateCommand(
            CreateGitHubIssueAsync,
            _logger);

    public bool CanResetDownloadStore
    {
        get => _canResetDownloadStore;
        private set => SetProperty(ref _canResetDownloadStore, value);
    }

    public RelayCommand ResetDownloadStoreCommand =>
        _resetDownloadStoreCommand ??= new RelayCommand(() =>
            CloseDialog(AppDialogOutcome.Accepted));

    public override void OnDialogOpened(AppDialogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var failure = GetRequiredParameter<Exception>(request, "failure");
        DiagnosticText = CreateDiagnosticText(failure);
        _issueDiagnosticText = CreateIssueDiagnosticText(failure);
        CanResetDownloadStore = DownloadStoreRecoveryPresenter.CanReset(failure);
        FailureMessageResourceKey = CanResetDownloadStore
            ? "DownloadStoreSchemaMismatchMessage"
            : "DownloadRuntimeFailureMessage";
        FailureMessage = DictionaryResource.GetString(FailureMessageResourceKey);
    }

    internal async Task CopyErrorDetailsAsync()
    {
        await _clipboardService.SetTextAsync(DiagnosticText).ConfigureAwait(true);
        _notifications.Show(DictionaryResource.GetString("ErrorDetailsCopied"));
    }

    internal async Task CreateGitHubIssueAsync()
    {
        try
        {
            var packagePath = await _logService.ExportFeedbackPackageAsync().ConfigureAwait(true);
            var issueUri = GitHubIssueUriBuilder.CreateForm(
                IssueTitle,
                IssueTemplate,
                "error_details",
                _issueDiagnosticText,
                TruncatedDiagnosticSuffix);
            if (!await _platformLauncher.OpenUriAsync(issueUri).ConfigureAwait(true))
            {
                _notifications.Show(DictionaryResource.GetString("OpenGitHubIssueFailed"));
            }

            var directory = System.IO.Path.GetDirectoryName(packagePath)
                ?? throw new InvalidOperationException("Feedback package directory is unavailable.");
            if (!await _platformLauncher.OpenFolderAsync(directory).ConfigureAwait(true))
            {
                _notifications.Show("无法打开诊断包所在文件夹");
            }
        }
        catch (Exception exception) when (exception is System.IO.IOException
            or UnauthorizedAccessException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            _logger.LogErrorMessage("Feedback package export failed.", exception);
            _notifications.Show(DictionaryResource.GetString("DiagnosticLogExportFailed"));
        }
    }

    private string CreateDiagnosticText(Exception failure)
    {
        var exceptionText = RedactDiagnosticText(failure.ToString());
        var operatingSystem = RedactDiagnosticText(
            RuntimeInformation.OSDescription);
        return $"""
            DownKyi version: {new AppInfo().VersionName}
            Operating system: {operatingSystem}
            Architecture: {RuntimeInformation.OSArchitecture}
            Failure boundary: Download bootstrap

            {exceptionText}
            """;
    }

    private static string CreateIssueDiagnosticText(Exception failure)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Safe diagnostic summary");
        builder.Append("DownKyi version: ").AppendLine(new AppInfo().VersionName);
        builder.Append("Operating system: ").AppendLine(RuntimeInformation.OSDescription);
        builder.Append("OS architecture: ").AppendLine(RuntimeInformation.OSArchitecture.ToString());
        builder.Append("Process architecture: ").AppendLine(RuntimeInformation.ProcessArchitecture.ToString());
        builder.Append(".NET runtime: ").AppendLine(Environment.Version.ToString());
        builder.AppendLine("Failure boundary: Download bootstrap");

        builder.AppendLine(SafeExceptionDiagnosticFormatter.Format(failure));

        builder.AppendLine("Attach the generated downkyi-feedback.zip file below.");
        return builder.ToString().TrimEnd();
    }

    private string RedactDiagnosticText(string? text)
    {
        var resourceRedacted = ExternalResourceRedactor.Redact(text);
        return _logService.RedactDiagnosticText(resourceRedacted);
    }
}
