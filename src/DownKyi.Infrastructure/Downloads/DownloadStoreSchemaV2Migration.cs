using DownKyi.Domain.Downloads;
using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Downloads;

internal static class DownloadStoreSchemaV2Migration
{
    private enum VersionTwoColumn
    {
        BaseVersion,
        BaseCreatedAtUtc,
        BaseUpdatedAtUtc,
        DownloadingPhase,
        DownloadingFailureCode,
        DownloadingFailureMessage,
        DownloadingFailureTransient,
        DownloadingDownloadedBytes,
        DownloadingTotalBytes,
        DownloadingBytesPerSecond
    }

    public static async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset appliedAtUtc,
        CancellationToken cancellationToken)
    {
        foreach (var column in Enum.GetValues<VersionTwoColumn>())
        {
            await AddColumnIfMissingAsync(connection, transaction, column, cancellationToken).ConfigureAwait(false);
        }

        const string phaseSql = """
            UPDATE downloading
            SET phase = CASE download_status
                WHEN 2 THEN @pausing
                WHEN 3 THEN @paused
                WHEN 4 THEN @downloading
                WHEN 5 THEN @queued
                WHEN 6 THEN @failed
                ELSE @queued
            END
            WHERE phase IS NULL;
            """;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = phaseSql;
            command.Parameters.AddWithValue("@pausing", (int)DownloadPhase.Pausing);
            command.Parameters.AddWithValue("@paused", (int)DownloadPhase.Paused);
            command.Parameters.AddWithValue("@downloading", (int)DownloadPhase.Downloading);
            command.Parameters.AddWithValue("@failed", (int)DownloadPhase.Failed);
            command.Parameters.AddWithValue("@queued", (int)DownloadPhase.Queued);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await DownloadStoreSchemaLifecycle
            .RecordMigrationAsync(connection, transaction, 2, appliedAtUtc, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task AddColumnIfMissingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        VersionTwoColumn column,
        CancellationToken cancellationToken)
    {
        using var inspect = connection.CreateCommand();
        inspect.Transaction = transaction;
        if (IsBaseColumn(column))
        {
            inspect.CommandText = "PRAGMA table_info(download_base)";
        }
        else
        {
            inspect.CommandText = "PRAGMA table_info(downloading)";
        }

        var columnName = GetColumnName(column);
        using (var reader = await inspect.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.GetString(1).Equals(columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        using var alter = connection.CreateCommand();
        alter.Transaction = transaction;
        SetAlterColumnSql(alter, column);
        await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool IsBaseColumn(VersionTwoColumn column) => column is
        VersionTwoColumn.BaseVersion or
        VersionTwoColumn.BaseCreatedAtUtc or
        VersionTwoColumn.BaseUpdatedAtUtc;

    private static string GetColumnName(VersionTwoColumn column) => column switch
    {
        VersionTwoColumn.BaseVersion => "version",
        VersionTwoColumn.BaseCreatedAtUtc => "created_at_utc",
        VersionTwoColumn.BaseUpdatedAtUtc => "updated_at_utc",
        VersionTwoColumn.DownloadingPhase => "phase",
        VersionTwoColumn.DownloadingFailureCode => "failure_code",
        VersionTwoColumn.DownloadingFailureMessage => "failure_message",
        VersionTwoColumn.DownloadingFailureTransient => "failure_transient",
        VersionTwoColumn.DownloadingDownloadedBytes => "downloaded_bytes",
        VersionTwoColumn.DownloadingTotalBytes => "total_bytes",
        VersionTwoColumn.DownloadingBytesPerSecond => "bytes_per_second",
        _ => throw new ArgumentOutOfRangeException(nameof(column))
    };

    private static void SetAlterColumnSql(SqliteCommand command, VersionTwoColumn column)
    {
        switch (column)
        {
            case VersionTwoColumn.BaseVersion:
                command.CommandText =
                    "ALTER TABLE download_base ADD COLUMN version INTEGER NOT NULL DEFAULT 0";
                break;
            case VersionTwoColumn.BaseCreatedAtUtc:
                command.CommandText =
                    "ALTER TABLE download_base ADD COLUMN created_at_utc INTEGER NOT NULL DEFAULT 0";
                break;
            case VersionTwoColumn.BaseUpdatedAtUtc:
                command.CommandText =
                    "ALTER TABLE download_base ADD COLUMN updated_at_utc INTEGER NOT NULL DEFAULT 0";
                break;
            case VersionTwoColumn.DownloadingPhase:
                command.CommandText = "ALTER TABLE downloading ADD COLUMN phase INTEGER";
                break;
            case VersionTwoColumn.DownloadingFailureCode:
                command.CommandText = "ALTER TABLE downloading ADD COLUMN failure_code TEXT";
                break;
            case VersionTwoColumn.DownloadingFailureMessage:
                command.CommandText = "ALTER TABLE downloading ADD COLUMN failure_message TEXT";
                break;
            case VersionTwoColumn.DownloadingFailureTransient:
                command.CommandText = "ALTER TABLE downloading ADD COLUMN failure_transient INTEGER";
                break;
            case VersionTwoColumn.DownloadingDownloadedBytes:
                command.CommandText = "ALTER TABLE downloading ADD COLUMN downloaded_bytes INTEGER";
                break;
            case VersionTwoColumn.DownloadingTotalBytes:
                command.CommandText = "ALTER TABLE downloading ADD COLUMN total_bytes INTEGER";
                break;
            case VersionTwoColumn.DownloadingBytesPerSecond:
                command.CommandText =
                    "ALTER TABLE downloading ADD COLUMN bytes_per_second INTEGER NOT NULL DEFAULT 0";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(column));
        }
    }
}
