namespace DownKyi.Application.Desktop;

public sealed record PublicationNavigationZone(
    long TypeId,
    string Name,
    int Count);

public sealed record PublicationNavigationPayload(
    long Mid,
    long SelectedTypeId,
    IReadOnlyList<PublicationNavigationZone> Zones)
{
    public static PublicationNavigationPayload All(long mid)
    {
        if (mid <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(mid), mid, "The uploader MID must be positive.");
        }

        return new PublicationNavigationPayload(mid, 0, Array.Empty<PublicationNavigationZone>());
    }
}
