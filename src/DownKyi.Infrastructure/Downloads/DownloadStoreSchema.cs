using System.Globalization;
using DownKyi.Application.Time;
using DownKyi.Domain.Downloads;
using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Downloads;

internal static class DownloadStoreSchema
{
    public const int CurrentVersion = 2;

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

    public static async Task InitializeAsync(
        SqliteConnection connection,
        string databasePath,
        bool databaseExisted,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var currentVersion = await ReadUserVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        if (currentVersion > CurrentVersion)
        {
            throw new InvalidOperationException(
                $"Download database schema {currentVersion} is newer than supported schema {CurrentVersion}.");
        }

        if (databaseExisted && currentVersion < CurrentVersion)
        {
            await BackupAsync(connection, databasePath, currentVersion, clock, cancellationToken).ConfigureAwait(false);
        }

        if (currentVersion == CurrentVersion)
        {
            return;
        }

        using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            if (currentVersion < 1)
            {
                await ApplyVersionOneAsync(connection, transaction, clock.UtcNow, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (currentVersion < 2)
            {
                await ApplyVersionTwoAsync(connection, transaction, clock.UtcNow, cancellationToken)
                    .ConfigureAwait(false);
            }

            await SetUserVersionAsync(connection, transaction, CurrentVersion, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ApplyVersionOneAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset appliedAtUtc,
        CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS download_base (
                id                    TEXT PRIMARY KEY,
                need_download_content TEXT NOT NULL DEFAULT '{}',
                bvid                  TEXT NOT NULL DEFAULT '',
                avid                  INTEGER NOT NULL DEFAULT 0,
                cid                   INTEGER NOT NULL DEFAULT 0,
                episode_id            INTEGER NOT NULL DEFAULT 0,
                cover_url             TEXT NOT NULL DEFAULT '',
                page_cover_url        TEXT NOT NULL DEFAULT '',
                zone_id               INTEGER NOT NULL DEFAULT 0,
                [order]               INTEGER NOT NULL DEFAULT 0,
                main_title            TEXT NOT NULL DEFAULT '',
                name                  TEXT NOT NULL DEFAULT '',
                duration              TEXT NOT NULL DEFAULT '',
                video_codec_name      TEXT NOT NULL DEFAULT '',
                resolution            TEXT NOT NULL DEFAULT '{}',
                audio_codec           TEXT,
                file_path             TEXT NOT NULL DEFAULT '',
                file_size             TEXT,
                page                  INTEGER NOT NULL DEFAULT 1
            );

            CREATE TABLE IF NOT EXISTS downloading (
                id                    TEXT PRIMARY KEY REFERENCES download_base(id) ON DELETE CASCADE,
                gid                   TEXT,
                download_files        TEXT NOT NULL DEFAULT '{}',
                downloaded_files      TEXT NOT NULL DEFAULT '[]',
                play_stream_type      INTEGER NOT NULL DEFAULT 0,
                download_status       INTEGER NOT NULL DEFAULT 0,
                download_content      TEXT,
                download_status_title TEXT,
                progress              REAL NOT NULL DEFAULT 0,
                downloading_file_size TEXT,
                max_speed             INTEGER NOT NULL DEFAULT 0,
                speed_display         TEXT
            );

            CREATE TABLE IF NOT EXISTS downloaded (
                id                    TEXT PRIMARY KEY REFERENCES download_base(id) ON DELETE CASCADE,
                max_speed_display     TEXT,
                finished_timestamp    INTEGER NOT NULL DEFAULT 0,
                finished_time         TEXT NOT NULL DEFAULT ''
            );

            CREATE TABLE IF NOT EXISTS download_schema_migrations (
                version               INTEGER PRIMARY KEY,
                applied_at_utc        INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS download_quarantine (
                quarantine_id         INTEGER PRIMARY KEY AUTOINCREMENT,
                source_table          TEXT NOT NULL,
                record_id             TEXT NOT NULL,
                field_name            TEXT NOT NULL,
                reason                TEXT NOT NULL,
                quarantined_at_utc    INTEGER NOT NULL,
                UNIQUE(source_table, record_id)
            );

            CREATE INDEX IF NOT EXISTS ix_downloading_status ON downloading(download_status);
            CREATE INDEX IF NOT EXISTS ix_downloaded_finished_timestamp ON downloaded(finished_timestamp DESC, id DESC);
            CREATE INDEX IF NOT EXISTS ix_download_base_main_title_order ON download_base(main_title, [order]);
            """;

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await RecordMigrationAsync(connection, transaction, 1, appliedAtUtc, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyVersionTwoAsync(
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

        await RecordMigrationAsync(connection, transaction, 2, appliedAtUtc, cancellationToken).ConfigureAwait(false);
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

    private static async Task RecordMigrationAsync(
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

    private static async Task<int> ReadUserVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static async Task SetUserVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int version,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(version, CurrentVersion);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version = 2";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task BackupAsync(
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
