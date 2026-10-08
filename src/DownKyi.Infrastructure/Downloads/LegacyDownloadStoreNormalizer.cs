using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;

namespace DownKyi.Infrastructure.Downloads;

internal static class LegacyDownloadStoreNormalizer
{
    public static LegacyDownloadNormalizationPlan Normalize(
        LegacyDownloadStoreSnapshot snapshot,
        IPhysicalOutputPathResolver physicalOutputPathResolver)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(physicalOutputPathResolver);

        var phaseUpdates = snapshot.Format.HasStateColumns
            ? []
            : snapshot.DownloadRows
                .Select(row => new LegacyPhaseUpdate(row.Id, (int)MapLegacyStatus(row.LegacyStatus)))
                .ToArray();
        var reservationUpdates = snapshot.Format.HasReservationKey
            ? []
            : CreateReservationUpdates(snapshot.DownloadRows);
        var quarantineRecordIds = snapshot.Format.HasAdmissionGate
            ? []
            : ClassifyUnsafePaths(snapshot.DownloadRows, physicalOutputPathResolver);
        var stagingTokenUpdates = snapshot.BaseRecords
            .Where(record => !Guid.TryParseExact(record.StagingToken, "N", out _))
            .Select(record => new LegacyStagingTokenUpdate(
                record.Id,
                Guid.NewGuid().ToString("N")))
            .ToArray();
        return new LegacyDownloadNormalizationPlan(
            phaseUpdates,
            reservationUpdates,
            quarantineRecordIds,
            stagingTokenUpdates);
    }

    private static List<LegacyReservationUpdate> CreateReservationUpdates(
        IEnumerable<LegacyDownloadRow> rows)
    {
        var updates = new List<LegacyReservationUpdate>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            string key;
            try
            {
                key = DownloadOutputPathKey.Create(
                    row.BasePath,
                    DownloadOutputPathKey.UsesCaseInsensitiveComparison);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
            {
                continue;
            }

            if (keys.Add(key))
            {
                updates.Add(new LegacyReservationUpdate(row.Id, key));
            }
        }

        return updates;
    }

    private static string[] ClassifyUnsafePaths(
        IEnumerable<LegacyDownloadRow> rows,
        IPhysicalOutputPathResolver physicalOutputPathResolver)
    {
        var resolutions = rows
            .Select(row => ResolvePath(row, physicalOutputPathResolver))
            .ToArray();
        var aliasedPhysicalKeys = resolutions
            .Where(resolution => resolution.OriginalKeyDiffersFromPhysical)
            .Select(resolution => resolution.PhysicalKey!)
            .ToHashSet(StringComparer.Ordinal);
        return resolutions
            .Where(resolution => resolution.PhysicalKey is null
                                 || aliasedPhysicalKeys.Contains(resolution.PhysicalKey))
            .Select(resolution => resolution.Id)
            .ToArray();
    }

    private static LegacyPathResolution ResolvePath(
        LegacyDownloadRow row,
        IPhysicalOutputPathResolver physicalOutputPathResolver)
    {
        try
        {
            var ignoreCase = DownloadOutputPathKey.UsesCaseInsensitiveComparison;
            var originalKey = DownloadOutputPathKey.Create(row.BasePath, ignoreCase);
            var physicalKey = DownloadOutputPathKey.Create(
                physicalOutputPathResolver.ResolvePhysicalBasePath(row.BasePath),
                ignoreCase);
            return new LegacyPathResolution(
                row.Id,
                physicalKey,
                !StringComparer.Ordinal.Equals(originalKey, physicalKey));
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            return new LegacyPathResolution(row.Id, null, false);
        }
    }

    private static DownloadPhase MapLegacyStatus(int status) => status switch
    {
        2 => DownloadPhase.Pausing,
        3 => DownloadPhase.Paused,
        4 => DownloadPhase.Downloading,
        5 => DownloadPhase.Queued,
        6 => DownloadPhase.Failed,
        _ => DownloadPhase.Queued
    };

    private sealed record LegacyPathResolution(
        string Id,
        string? PhysicalKey,
        bool OriginalKeyDiffersFromPhysical);
}
