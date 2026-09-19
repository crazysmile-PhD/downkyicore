using System;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DownKyi.Application.Desktop;
using DownKyi.Application.Diagnostics;
using DownKyi.Commands;
using DownKyi.Models;
using DownKyi.Utils;
using Microsoft.Extensions.Logging;

namespace DownKyi.ViewModels.Dialogs;

internal sealed partial class DownloadRuntimeFailureDialogViewModel : BaseDialogViewModel
{
    private const int MaximumIssueUriLength = 8_000;
    private const string IssueTitle = "Download system failed to initialize";
    private const string TruncatedDiagnosticSuffix =
        "\n\n[Diagnostic text truncated to keep the pre-filled issue URL within a safe length. " +
        "Use Copy Error Details for the complete text.]";

    private readonly IApplicationLogService _logService;
    private readonly IClipboardService _clipboardService;
    private readonly IPlatformLauncher _platformLauncher;
    private readonly IUserNotificationService _notifications;
    private readonly ILogger<DownloadRuntimeFailureDialogViewModel> _logger;
    private DownKyiAsyncDelegateCommand? _copyErrorDetailsCommand;
    private DownKyiAsyncDelegateCommand? _createGitHubIssueCommand;
    private string _diagnosticText = string.Empty;

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
    }

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

    public override void OnDialogOpened(AppDialogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var failure = GetRequiredParameter<Exception>(request, "failure");
        DiagnosticText = CreateDiagnosticText(failure);
    }

    internal async Task CopyErrorDetailsAsync()
    {
        await _clipboardService.SetTextAsync(DiagnosticText).ConfigureAwait(true);
        _notifications.Show(DictionaryResource.GetString("ErrorDetailsCopied"));
    }

    internal async Task CreateGitHubIssueAsync()
    {
        var issueUri = CreateGitHubIssueUri(DiagnosticText);
        if (!await _platformLauncher.OpenUriAsync(issueUri).ConfigureAwait(true))
        {
            _notifications.Show(DictionaryResource.GetString("OpenGitHubIssueFailed"));
        }
    }

    private static Uri CreateGitHubIssueUri(string diagnosticText)
    {
        var prefix =
            $"https://github.com/{AppConstant.RepoOwner}/{AppConstant.RepoName}/issues/new" +
            $"?title={Uri.EscapeDataString(IssueTitle)}&body=";
        var encodedDiagnostic = Uri.EscapeDataString(diagnosticText);
        if (prefix.Length + encodedDiagnostic.Length <= MaximumIssueUriLength)
        {
            return new Uri(prefix + encodedDiagnostic);
        }

        var low = 0;
        var high = diagnosticText.Length;
        var bestLength = 0;
        while (low <= high)
        {
            var midpoint = low + ((high - low) / 2);
            var candidateLength = GetSafePrefixLength(diagnosticText, midpoint);
            var candidateBody = diagnosticText[..candidateLength] + TruncatedDiagnosticSuffix;
            if (prefix.Length + Uri.EscapeDataString(candidateBody).Length <= MaximumIssueUriLength)
            {
                bestLength = Math.Max(bestLength, candidateLength);
                low = midpoint + 1;
            }
            else
            {
                high = midpoint - 1;
            }
        }

        var body = diagnosticText[..bestLength] + TruncatedDiagnosticSuffix;
        return new Uri(prefix + Uri.EscapeDataString(body));
    }

    private static int GetSafePrefixLength(string text, int length)
    {
        if (length > 0 && length < text.Length &&
            char.IsHighSurrogate(text[length - 1]) && char.IsLowSurrogate(text[length]))
        {
            return length - 1;
        }

        return length;
    }

    [GeneratedRegex(
        "(^|[^\\w])(?:[a-z][a-z0-9+.-]*://|[a-z]:[\\\\/]|[\\\\/])[^\\r\\n]*",
        RegexOptions.IgnoreCase |
        RegexOptions.Multiline |
        RegexOptions.CultureInvariant |
        RegexOptions.NonBacktracking)]
    private static partial Regex ExternalResourceLineRegex();

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

    private string RedactDiagnosticText(string? text)
    {
        var resourceRedacted = ExternalResourceLineRegex().Replace(
            text ?? string.Empty,
            "$1[resource redacted]");
        return _logService.RedactDiagnosticText(resourceRedacted);
    }
}
