namespace DownKyi.Application.Desktop;

public enum AppDialog
{
    Alert = 0,
    DownloadSettings = 1,
    ParsingSelector = 2,
    AlreadyDownloaded = 3,
    NewVersionAvailable = 4,
    LegacyUpgrade = 5
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
