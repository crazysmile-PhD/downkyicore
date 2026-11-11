namespace DownKyi.Application.Desktop;

public enum AppDialog
{
    Alert = 0,
    DownloadSettings = 1,
    NewVersionAvailable = 3,
    LegacyUpgrade = 4,
    StartupDiagnostics = 5,
    DownloadContentConflict = 6,
    DownloadDuplicateBatch = 7,
    DownloadUnavailableBatch = 8
}

public enum AppDialogOutcome
{
    None = 0,
    Accepted = 1,
    Rejected = 2,
    Canceled = 3
}

public sealed record AppDialogRequest(
    AppDialog Dialog,
    IReadOnlyDictionary<string, object?>? Parameters = null);

public sealed record AppDialogResult(
    AppDialogOutcome Outcome,
    IReadOnlyDictionary<string, object?> Parameters);

public interface IAppDialogService
{
    Task<AppDialogResult> ShowAsync(
        AppDialogRequest request,
        CancellationToken cancellationToken = default);
}
