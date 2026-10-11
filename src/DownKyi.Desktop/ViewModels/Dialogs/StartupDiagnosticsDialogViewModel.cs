using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Application.Diagnostics;
using DownKyi.Commands;
using DownKyi.Infrastructure.Logging;
using DownKyi.Models;
using DownKyi.Services;
using DownKyi.Services.Startup;
using DownKyi.Utils;
using Microsoft.Extensions.Logging;

namespace DownKyi.ViewModels.Dialogs;

internal sealed class StartupDiagnosticsDialogViewModel : BaseDialogViewModel
{
    internal const string DiagnosticsParameter = "diagnostics";
    internal const string RecoveryDiagnosticParameter = "recoveryDiagnostic";
    internal const string BuiltInFallbackDiagnosticParameter = "builtInFallbackDiagnostic";
    private const string IssueTitle = "DownKyi startup diagnostics report";
    private const string IssueTemplate = "app_diagnostic_report.yml";
    private const string TruncatedDiagnosticSuffix =
        "\n\n[The summary was truncated; attach the generated diagnostic ZIP.]";

    private readonly IApplicationLogService _logService;
    private readonly IClipboardService _clipboardService;
    private readonly IPlatformLauncher _platformLauncher;
    private readonly IUserNotificationService _notifications;
    private readonly ILogger<StartupDiagnosticsDialogViewModel> _logger;
    private DownKyiAsyncDelegateCommand? _copyAllDiagnosticsCommand;
    private DownKyiAsyncDelegateCommand? _createGitHubIssueCommand;
    private IReadOnlyList<StartupDiagnosticItemViewModel> _items = [];
    private string _introMessage;
    private string _diagnosticText = string.Empty;
    private string _issueDiagnosticText = string.Empty;

    public StartupDiagnosticsDialogViewModel(
        IApplicationLogService logService,
        IClipboardService clipboardService,
        IPlatformLauncher platformLauncher,
        IUserNotificationService notifications,
        ILogger<StartupDiagnosticsDialogViewModel> logger)
    {
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _clipboardService = clipboardService
            ?? throw new ArgumentNullException(nameof(clipboardService));
        _platformLauncher = platformLauncher
            ?? throw new ArgumentNullException(nameof(platformLauncher));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Title = DictionaryResource.GetString("StartupDiagnosticsTitle");
        _introMessage = DictionaryResource.GetString("StartupDiagnosticsMessage");
    }

    public string IntroMessage
    {
        get => _introMessage;
        private set => SetProperty(ref _introMessage, value);
    }

    public IReadOnlyList<StartupDiagnosticItemViewModel> Items
    {
        get => _items;
        private set => SetProperty(ref _items, value);
    }

    public string DiagnosticText
    {
        get => _diagnosticText;
        private set => SetProperty(ref _diagnosticText, value);
    }

    public DownKyiAsyncDelegateCommand CopyAllDiagnosticsCommand =>
        _copyAllDiagnosticsCommand ??= new DownKyiAsyncDelegateCommand(
            CopyAllDiagnosticsAsync,
            _logger);

    public DownKyiAsyncDelegateCommand CreateGitHubIssueCommand =>
        _createGitHubIssueCommand ??= new DownKyiAsyncDelegateCommand(
            CreateGitHubIssueAsync,
            _logger);

    public override void OnDialogOpened(AppDialogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var diagnostics = GetRequiredParameter<IReadOnlyList<StartupDiagnostic>>(
            request,
            DiagnosticsParameter);
        if (diagnostics.Count == 0)
        {
            throw new InvalidOperationException(
                "The startup diagnostics dialog requires at least one diagnostic.");
        }

        Title = FormatResource(
            "StartupDiagnosticsTitleFormat",
            diagnostics.Count.ToString(CultureInfo.CurrentCulture));
        IntroMessage = DictionaryResource.GetString("StartupDiagnosticsMessage");
        Items = diagnostics
            .Select((diagnostic, index) => CreateItem(diagnostic, index + 1))
            .ToArray();
        DiagnosticText = CreateDiagnosticText(diagnostics);
        _issueDiagnosticText = CreateIssueDiagnosticText(diagnostics);
    }

    internal async Task CopyAllDiagnosticsAsync()
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
                _notifications.Show(DictionaryResource.GetString("OpenDiagnosticFolderFailed"));
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

    private StartupDiagnosticItemViewModel CreateItem(
        StartupDiagnostic diagnostic,
        int index)
    {
        var (titleResourceKey, descriptionResourceKey, impactResourceKey) =
            GetPresentationResourceKeys(diagnostic.Kind);
        var title = DictionaryResource.GetString(titleResourceKey);
        return new StartupDiagnosticItemViewModel(
            diagnostic,
            string.Create(CultureInfo.CurrentCulture, $"{index}. {title}"),
            DictionaryResource.GetString(descriptionResourceKey),
            FormatResource(
                "StartupDiagnosticsImpactFormat",
                DictionaryResource.GetString(impactResourceKey)),
            CreateItemDiagnosticText(diagnostic),
            RequestRecovery,
            RequestBuiltInFallback);
    }

    private static (string Title, string Description, string Impact)
        GetPresentationResourceKeys(StartupDiagnosticKind kind)
    {
        return kind switch
        {
            StartupDiagnosticKind.LegacyMigrationFailed => (
                "StartupLegacyMigrationFailureTitle",
                "StartupLegacyMigrationFailureDescription",
                "StartupLegacyMigrationFailureImpact"),
            StartupDiagnosticKind.DownloadRuntimeMissing => (
                "StartupDownloadFailureTitle",
                "StartupDownloadMissingDescription",
                "StartupDownloadFailureImpact"),
            StartupDiagnosticKind.DownloadStoreSchemaMismatch => (
                "StartupDownloadStoreFailureTitle",
                "StartupDownloadStoreFailureDescription",
                "StartupDownloadStoreFailureImpact"),
            StartupDiagnosticKind.DownloadRuntimeFailed => (
                "StartupDownloadFailureTitle",
                "StartupDownloadFailureDescription",
                "StartupDownloadFailureImpact"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
    }

    private void RequestRecovery(StartupDiagnostic diagnostic)
    {
        if (!diagnostic.AllowsDownloadStoreReset)
        {
            return;
        }

        CloseDialog(
            AppDialogOutcome.Accepted,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [RecoveryDiagnosticParameter] = diagnostic
            });
    }

    private void RequestBuiltInFallback(StartupDiagnostic diagnostic)
    {
        if (!diagnostic.AllowsBuiltInDownloaderFallback)
        {
            return;
        }

        CloseDialog(
            AppDialogOutcome.Accepted,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [BuiltInFallbackDiagnosticParameter] = diagnostic
            });
    }

    private string CreateDiagnosticText(IReadOnlyList<StartupDiagnostic> diagnostics)
    {
        var builder = new StringBuilder();
        builder.Append("DownKyi version: ").AppendLine(new AppInfo().VersionName);
        builder.Append("Operating system: ").AppendLine(RedactDiagnosticText(
            RuntimeInformation.OSDescription));
        builder.Append("Architecture: ").AppendLine(RuntimeInformation.OSArchitecture.ToString());
        builder.Append("Startup issue count: ").AppendLine(
            diagnostics.Count.ToString(CultureInfo.InvariantCulture));

        for (var index = 0; index < diagnostics.Count; index++)
        {
            builder.AppendLine();
            builder.Append("===== Startup issue ")
                .Append(index + 1)
                .AppendLine(" =====");
            builder.Append(CreateItemDiagnosticText(diagnostics[index]));
        }

        return builder.ToString().TrimEnd();
    }

    private string CreateItemDiagnosticText(StartupDiagnostic diagnostic)
    {
        var builder = new StringBuilder();
        builder.Append("Source: ").AppendLine(diagnostic.Source.ToString());
        builder.Append("Type: ").AppendLine(diagnostic.Kind.ToString());
        if (!string.IsNullOrWhiteSpace(diagnostic.Context))
        {
            builder.Append("Context: ").AppendLine(RedactDiagnosticText(diagnostic.Context));
        }

        builder.AppendLine(RedactDiagnosticText(diagnostic.Failure.ToString()));
        return builder.ToString().TrimEnd();
    }

    private static string CreateIssueDiagnosticText(
        IReadOnlyList<StartupDiagnostic> diagnostics)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Safe diagnostic summary");
        builder.Append("DownKyi version: ").AppendLine(new AppInfo().VersionName);
        builder.Append("Operating system: ").AppendLine(RuntimeInformation.OSDescription);
        builder.Append("OS architecture: ").AppendLine(RuntimeInformation.OSArchitecture.ToString());
        builder.Append("Process architecture: ")
            .AppendLine(RuntimeInformation.ProcessArchitecture.ToString());
        builder.Append(".NET runtime: ").AppendLine(Environment.Version.ToString());
        builder.Append("Startup issue count: ").AppendLine(
            diagnostics.Count.ToString(CultureInfo.InvariantCulture));

        for (var index = 0; index < diagnostics.Count; index++)
        {
            var diagnostic = diagnostics[index];
            builder.AppendLine();
            builder.Append("Issue ").Append(index + 1).AppendLine(":");
            builder.Append("Source: ").AppendLine(diagnostic.Source.ToString());
            builder.Append("Type: ").AppendLine(diagnostic.Kind.ToString());
            builder.AppendLine(SafeExceptionDiagnosticFormatter.Format(diagnostic.Failure));
        }

        builder.AppendLine();
        builder.AppendLine("Attach the generated downkyi-feedback.zip file below.");
        return builder.ToString().TrimEnd();
    }

    private string RedactDiagnosticText(string? text)
    {
        var resourceRedacted = ExternalResourceRedactor.Redact(text);
        return _logService.RedactDiagnosticText(resourceRedacted);
    }

    private static string FormatResource(string resourceKey, string value)
    {
        return DictionaryResource.GetString(resourceKey)
            .Replace("{0}", value, StringComparison.Ordinal);
    }
}

internal sealed class StartupDiagnosticItemViewModel : ObservableObject
{
    private bool _isExpanded;

    public StartupDiagnosticItemViewModel(
        StartupDiagnostic diagnostic,
        string title,
        string description,
        string impact,
        string diagnosticText,
        Action<StartupDiagnostic> requestRecovery,
        Action<StartupDiagnostic> requestBuiltInFallback)
    {
        Diagnostic = diagnostic ?? throw new ArgumentNullException(nameof(diagnostic));
        Title = title ?? throw new ArgumentNullException(nameof(title));
        Description = description ?? throw new ArgumentNullException(nameof(description));
        Impact = impact ?? throw new ArgumentNullException(nameof(impact));
        DiagnosticText = diagnosticText ?? throw new ArgumentNullException(nameof(diagnosticText));
        ArgumentNullException.ThrowIfNull(requestRecovery);
        ArgumentNullException.ThrowIfNull(requestBuiltInFallback);
        ResetDownloadStoreCommand = new RelayCommand(() => requestRecovery(diagnostic));
        UseBuiltInDownloaderCommand = new RelayCommand(
            () => requestBuiltInFallback(diagnostic));
        ToggleDetailsCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    internal StartupDiagnostic Diagnostic { get; }

    public string Title { get; }

    public string Description { get; }

    public string Impact { get; }

    public string DiagnosticText { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        private set => SetProperty(ref _isExpanded, value);
    }

    public bool CanResetDownloadStore =>
        Diagnostic.AllowsDownloadStoreReset;

    public bool CanUseBuiltInDownloader =>
        Diagnostic.AllowsBuiltInDownloaderFallback;

    public RelayCommand ResetDownloadStoreCommand { get; }

    public RelayCommand UseBuiltInDownloaderCommand { get; }

    public RelayCommand ToggleDetailsCommand { get; }
}
