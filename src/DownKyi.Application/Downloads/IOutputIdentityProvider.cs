namespace DownKyi.Application.Downloads;

/// <summary>
/// Converts an output base path into the identity used for durable
/// reservation arbitration.
///
/// This identity is intentionally separate from the logical path that is
/// stored on the download task.
/// </summary>
public interface IOutputIdentityProvider
{
    string CreateReservationKey(
        string basePath,
        bool ignoreCase);
}
