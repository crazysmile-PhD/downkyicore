using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Downloads;

internal static class LegacyDownloadStoreReader
{
    public static async Task<LegacyDownloadStoreSnapshot> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LegacyDownloadStoreFormat format,
        CancellationToken cancellationToken)
    {
        if (!format.IsSupported)
        {
            throw new DownloadStoreSchemaMismatchException(
                format.UserVersion,
                format.SchemaDifferences);
        }

        var downloadRows = await ReadDownloadRowsAsync(
            connection,
            transaction,
            format.HasReservationKey,
            cancellationToken).ConfigureAwait(false);
        var baseRecords = await ReadBaseRecordsAsync(
            connection,
            transaction,
            format.HasStagingToken,
            cancellationToken).ConfigureAwait(false);
        return new LegacyDownloadStoreSnapshot(format, downloadRows, baseRecords);
    }

    private static async Task<IReadOnlyList<LegacyDownloadRow>> ReadDownloadRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        bool hasReservationKey,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (hasReservationKey)
        {
            command.CommandText = """
                SELECT db.id, db.file_path, dl.download_status, db.output_reservation_key
                FROM download_base db
                INNER JOIN downloading dl ON dl.id = db.id
                ORDER BY db.id
                """;
        }
        else
        {
            command.CommandText = """
                SELECT db.id, db.file_path, dl.download_status, NULL
                FROM download_base db
                INNER JOIN downloading dl ON dl.id = db.id
                ORDER BY db.id
                """;
        }

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<LegacyDownloadRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new LegacyDownloadRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false)
                    ? null : reader.GetString(3)));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<LegacyBaseRecord>> ReadBaseRecordsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        bool hasStagingToken,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (hasStagingToken)
        {
            command.CommandText = "SELECT id, staging_token FROM download_base ORDER BY id";
        }
        else
        {
            command.CommandText = "SELECT id FROM download_base ORDER BY id";
        }
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<LegacyBaseRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new LegacyBaseRecord(
                reader.GetString(0),
                !hasStagingToken
                    || await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false)
                        ? null
                        : reader.GetString(1)));
        }

        return rows;
    }
}
