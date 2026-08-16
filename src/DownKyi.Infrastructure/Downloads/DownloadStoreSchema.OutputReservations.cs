using System.Collections.Generic;
using System.IO;
using DownKyi.Application.Downloads;
using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Downloads;

internal static partial class DownloadStoreSchema
{    private static async Task ApplyVersionThreeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset appliedAtUtc,
        CancellationToken cancellationToken)
    {
        if (!await HasDownloadBaseColumnAsync(
                connection,
                transaction,
                OutputReservationColumn,
                cancellationToken).ConfigureAwait(false))
        {
            using var alter = connection.CreateCommand();
            alter.Transaction = transaction;
            alter.CommandText =
                $"ALTER TABLE download_base ADD COLUMN {OutputReservationColumn} TEXT";
            await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await BackfillOutputReservationsAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                CREATE INDEX IF NOT EXISTS ix_download_base_file_path
                    ON download_base(file_path);
                CREATE INDEX IF NOT EXISTS ix_download_base_file_path_nocase
                    ON download_base(file_path COLLATE NOCASE);
                CREATE UNIQUE INDEX IF NOT EXISTS ux_download_base_output_reservation
                    ON download_base(output_reservation_key)
                    WHERE output_reservation_key IS NOT NULL;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await RecordMigrationAsync(connection, transaction, 3, appliedAtUtc, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task ApplyVersionFourAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset appliedAtUtc,
        CancellationToken cancellationToken)
    {
        // Version 3 persisted lexical/Form-C reservation keys. Rebuild the
        // active set using filesystem-resolved identity.
        //
        // Clear all keys first. An existing database may already contain
        // two active rows that were admitted through different aliases to
        // the same physical destination. Keeping either legacy key while
        // rewriting another row could cause a transient UNIQUE collision.
        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = """
                DROP INDEX IF EXISTS ux_download_base_output_reservation;

                UPDATE download_base
                SET output_reservation_key = NULL;
                """;

            await clear
                .ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        await BackfillOutputReservationsAsync(
                connection,
                transaction,
                cancellationToken)
            .ConfigureAwait(false);

        using (var createIndex = connection.CreateCommand())
        {
            createIndex.Transaction = transaction;
            createIndex.CommandText = """
                CREATE UNIQUE INDEX IF NOT EXISTS ux_download_base_output_reservation
                    ON download_base(output_reservation_key)
                    WHERE output_reservation_key IS NOT NULL;
                """;

            await createIndex
                .ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        await RecordMigrationAsync(
                connection,
                transaction,
                4,
                appliedAtUtc,
                cancellationToken)
            .ConfigureAwait(false);
    }
    private static async Task BackfillOutputReservationsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var rows = new List<(string Id, string BasePath)>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT db.id, db.file_path
                FROM download_base db
                INNER JOIN downloading dl ON dl.id = db.id
                ORDER BY db.id
                """;
            using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

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

            if (!keys.Add(key))
            {
                continue;
            }

            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE download_base
                SET output_reservation_key = @key
                WHERE id = @id
                """;
            update.Parameters.AddWithValue("@key", key);
            update.Parameters.AddWithValue("@id", row.Id);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

}