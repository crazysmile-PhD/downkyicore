using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Downloads;

internal static class DownloadStoreSchemaV6Migration
{
    public static async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset appliedAtUtc,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "ALTER TABLE download_base ADD COLUMN published_artifacts TEXT NOT NULL DEFAULT '{}'";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await DownloadStoreSchemaLifecycle
            .RecordMigrationAsync(connection, transaction, 6, appliedAtUtc, cancellationToken)
            .ConfigureAwait(false);
    }
}
