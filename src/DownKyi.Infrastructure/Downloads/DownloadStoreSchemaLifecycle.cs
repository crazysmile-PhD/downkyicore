using System.Globalization;
using DownKyi.Application.Time;
using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Downloads;

internal static class DownloadStoreSchemaLifecycle
{
    public static async Task RecordMigrationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int version,
        DateTimeOffset appliedAtUtc,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR REPLACE INTO download_schema_migrations(version, applied_at_utc)
            VALUES (@version, @applied_at_utc)
            """;
        command.Parameters.AddWithValue("@version", version);
        command.Parameters.AddWithValue("@applied_at_utc", appliedAtUtc.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<int> ReadUserVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    public static async Task SetUserVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int version,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(version, 5);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version = 5";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task BackupAsync(
        SqliteConnection source,
        string databasePath,
        int sourceVersion,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(databasePath) ?? ".";
        var backupDirectory = Path.Combine(directory, "Backup");
        Directory.CreateDirectory(backupDirectory);
        var timestamp = clock.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        var backupPath = Path.Combine(
            backupDirectory,
            $"{Path.GetFileName(databasePath)}.schema-v{sourceVersion}-{timestamp}.bak");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();

        using var backup = new SqliteConnection(connectionString);
        await backup.OpenAsync(cancellationToken).ConfigureAwait(false);
        source.BackupDatabase(backup);
    }
}
