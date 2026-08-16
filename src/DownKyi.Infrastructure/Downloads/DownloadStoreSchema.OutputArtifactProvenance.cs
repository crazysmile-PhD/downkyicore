using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Downloads;

internal static partial class DownloadStoreSchema
{
    private static async Task ApplyVersionFiveAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset appliedAtUtc,
        CancellationToken cancellationToken)
    {
        // Final-output provenance begins at version 5. Existing task rows,
        // including their paths and transfer-file maps, are deliberately not
        // evidence of ownership and must not be backfilled here.
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS download_output_artifact_provenance (
                    task_id               TEXT NOT NULL REFERENCES download_base(id) ON DELETE CASCADE,
                    artifact_key          TEXT NOT NULL,
                    artifact_kind         TEXT NOT NULL,
                    canonical_path        TEXT NOT NULL,
                    byte_length           INTEGER NOT NULL CHECK(byte_length >= 0),
                    sha256                TEXT NOT NULL,
                    identity_provider     TEXT NOT NULL,
                    filesystem_identity   TEXT NOT NULL,
                    published_at_utc      INTEGER NOT NULL,
                    PRIMARY KEY(task_id, artifact_key)
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await RecordMigrationAsync(connection, transaction, 5, appliedAtUtc, cancellationToken)
            .ConfigureAwait(false);
    }
}
