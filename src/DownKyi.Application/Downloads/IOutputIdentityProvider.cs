namespace DownKyi.Application.Downloads;

/// <summary>
/// Creates the physical identity used to arbitrate ownership of an output
/// base stem while leaving the logical task path unchanged.
/// </summary>
public interface IOutputIdentityProvider
{
    string CreateReservationKey(string basePath, bool ignoreCase);
}
