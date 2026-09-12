using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Downloads;

internal static class DownloadStoreSchemaV7Migration
{
    public static async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset appliedAtUtc,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE download_base ADD COLUMN staging_token TEXT NOT NULL DEFAULT '';
            UPDATE download_base SET staging_token = lower(hex(randomblob(16)));
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await DownloadStoreSchemaLifecycle
            .RecordMigrationAsync(connection, transaction, 7, appliedAtUtc, cancellationToken)
            .ConfigureAwait(false);
    }
}
