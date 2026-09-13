using DownKyi.Domain.Results;

namespace DownKyi.Application.Downloads;

public static class DownloadAdmissionErrors
{
    public const string LegacyUpgradeBlockedCode = "download.admission.legacy_upgrade_blocked";

    public static OperationError LegacyUpgradeBlocked()
    {
        return new OperationError(
            LegacyUpgradeBlockedCode,
            "New downloads are blocked until the legacy remote-task risk is confirmed resolved.",
            OperationErrorKind.Conflict);
    }

    public static bool IsLegacyUpgradeBlocked(OperationError? error)
    {
        return string.Equals(error?.Code, LegacyUpgradeBlockedCode, StringComparison.Ordinal);
    }
}
