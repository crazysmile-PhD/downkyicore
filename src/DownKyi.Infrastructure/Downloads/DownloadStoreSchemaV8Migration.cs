using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Downloads;

internal static class DownloadStoreSchemaV8Migration
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
            ALTER TABLE download_base ADD COLUMN publishing_key TEXT;
            ALTER TABLE download_base ADD COLUMN publishing_file_name TEXT;
            ALTER TABLE download_base ADD COLUMN publishing_length INTEGER;
            ALTER TABLE download_base ADD COLUMN publishing_sha256 TEXT;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await DownloadStoreSchemaLifecycle
            .RecordMigrationAsync(connection, transaction, 8, appliedAtUtc, cancellationToken)
            .ConfigureAwait(false);
    }
}
