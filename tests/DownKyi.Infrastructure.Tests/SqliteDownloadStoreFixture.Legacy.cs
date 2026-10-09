using System.Collections.Immutable;
using DownKyi.Application.Downloads;
using DownKyi.Application.Time;
using DownKyi.Domain.Downloads;
using DownKyi.Infrastructure.Downloads;
using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Tests;

internal sealed partial class SqliteDownloadStoreFixture
{
    internal async Task CreateVersionThreeDatabaseAsync(params DownloadTask[] tasks)
    {
        using (var store = CreateStore())
        {
            await store.InitializeAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
            foreach (var task in tasks.Where(task => task.Phase != DownloadPhase.Completed))
            {
                Assert.True((await store.AddAsync(
                    task,
                    TestContext.Current.CancellationToken).ConfigureAwait(false)).IsSuccess);
            }
        }

        await DowngradeCurrentDatabaseAsync(
            3,
            tasks.Where(task => task.Phase == DownloadPhase.Completed)).ConfigureAwait(false);
    }

    internal async Task CreateVersionFourDatabaseAsync()
    {
        using (var store = CreateStore())
        {
            Assert.True((await store.AddAsync(
                CreatePausedTask(
                    "version-four",
                    "version-four-output",
                    requestedContent: DownloadContentSelection.None),
                TestContext.Current.CancellationToken).ConfigureAwait(false)).IsSuccess);
        }

        await DowngradeCurrentDatabaseAsync(4).ConfigureAwait(false);
    }

    internal async Task SimulateLegacyVersionWriteAsync()
    {
        using var connection = await OpenConnectionAsync(readOnly: false).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS downloaded (
                id TEXT PRIMARY KEY REFERENCES download_base(id) ON DELETE CASCADE,
                max_speed_display TEXT,
                finished_timestamp INTEGER NOT NULL DEFAULT 0,
                finished_time TEXT NOT NULL DEFAULT ''
            );

            INSERT INTO download_base
                (id, bvid, avid, cid, main_title, name, file_path)
            VALUES
                ('legacy-history', 'BV1LEGACYHISTORY', 10, 20, 'Legacy', 'History', 'legacy-history-output'),
                ('legacy-active', 'BV1LEGACYACTIVE', 11, 21, 'Legacy', 'Active', 'legacy-active-output');

            INSERT INTO downloaded
                (id, max_speed_display, finished_timestamp, finished_time)
            VALUES
                ('legacy-history', '1 MiB/s', 456, 'legacy-finished');

            INSERT INTO downloading
                (id, gid, download_files, downloaded_files, play_stream_type,
                 download_status, progress, max_speed)
            VALUES
                ('legacy-active', 'legacy-gid', '{"video":"video.m4s"}', '[]', 1, 3, 50, 1024);
            """;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    internal async Task CreateLegacyVersionDatabaseAsync(int version)
    {
        if (version is < 1 or > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }

        using (var store = CreateStore())
        {
            Assert.True((await store.AddAsync(
                CreatePausedTask($"legacy-v{version}"),
                TestContext.Current.CancellationToken).ConfigureAwait(false)).IsSuccess);
        }

        await DowngradeCurrentDatabaseAsync(version).ConfigureAwait(false);
    }

    internal async Task DowngradeCurrentDatabaseAsync(
        int version,
        IEnumerable<DownloadTask>? completedTasks = null)
    {
        using var connection = await OpenConnectionAsync(readOnly: false).ConfigureAwait(false);
        using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE downloaded (
                id TEXT PRIMARY KEY REFERENCES download_base(id) ON DELETE CASCADE,
                max_speed_display TEXT,
                finished_timestamp INTEGER NOT NULL DEFAULT 0,
                finished_time TEXT NOT NULL DEFAULT ''
            );
            DROP TABLE download_history;
            """;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);

        foreach (var task in completedTasks ?? [])
        {
            await InsertLegacyCompletedTaskAsync(connection, transaction, task).ConfigureAwait(false);
        }

        command.CommandText = """
            ALTER TABLE download_base DROP COLUMN publishing_key;
            ALTER TABLE download_base DROP COLUMN publishing_file_name;
            ALTER TABLE download_base DROP COLUMN publishing_length;
            ALTER TABLE download_base DROP COLUMN publishing_sha256;
            """;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);

        if (version < 7)
        {
            command.CommandText = "ALTER TABLE download_base DROP COLUMN staging_token";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        }

        if (version < 6)
        {
            command.CommandText = "ALTER TABLE download_base DROP COLUMN published_artifacts";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        }

        if (version < 5)
        {
            command.CommandText = "ALTER TABLE download_base DROP COLUMN nfo_request";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        }

        if (version < 4)
        {
            command.CommandText = "DROP TABLE download_upgrade_admission_gate";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        }

        if (version < 3)
        {
            command.CommandText = """
                DROP INDEX ux_download_base_output_reservation;
                DROP INDEX ix_download_base_file_path;
                DROP INDEX ix_download_base_file_path_nocase;
                ALTER TABLE download_base DROP COLUMN output_reservation_key;
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        }

        if (version < 2)
        {
            command.CommandText = """
                ALTER TABLE download_base DROP COLUMN version;
                ALTER TABLE download_base DROP COLUMN created_at_utc;
                ALTER TABLE download_base DROP COLUMN updated_at_utc;
                ALTER TABLE downloading DROP COLUMN phase;
                ALTER TABLE downloading DROP COLUMN failure_code;
                ALTER TABLE downloading DROP COLUMN failure_message;
                ALTER TABLE downloading DROP COLUMN failure_transient;
                ALTER TABLE downloading DROP COLUMN downloaded_bytes;
                ALTER TABLE downloading DROP COLUMN total_bytes;
                ALTER TABLE downloading DROP COLUMN bytes_per_second;
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        }

        command.CommandText = """
            DELETE FROM download_schema_migrations;
            WITH RECURSIVE versions(value) AS (
                SELECT 1
                UNION ALL
                SELECT value + 1 FROM versions WHERE value < @version
            )
            INSERT INTO download_schema_migrations(version, applied_at_utc)
            SELECT value, @applied_at_utc FROM versions;
            """;
        command.Parameters.AddWithValue("@version", version);
        command.Parameters.AddWithValue("@applied_at_utc", _clock.UtcNow.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        SetLegacyUserVersionCommandText(command, version);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    internal static async Task InsertLegacyCompletedTaskAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DownloadTask task)
    {
        await DownloadTaskSqlWriter.InsertBaseAsync(
            connection,
            transaction,
            task,
            TestContext.Current.CancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO downloaded
                (id, max_speed_display, finished_timestamp, finished_time)
            VALUES (@id, @max_speed_display, @finished_timestamp, @finished_time)
            """;
        command.Parameters.AddWithValue("@id", task.Id.Value);
        command.Parameters.AddWithValue(
            "@max_speed_display",
            task.Completion!.MaximumSpeedText ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@finished_timestamp", task.Completion.FinishedTimestamp);
        command.Parameters.AddWithValue("@finished_time", task.Completion.FinishedTimeText);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    internal async Task InsertLegacyQuarantineAsync(string sourceTable, string id)
    {
        using var connection = await OpenConnectionAsync(readOnly: false).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO download_quarantine
                (source_table, record_id, field_name, reason, quarantined_at_utc)
            VALUES (@source_table, @id, 'legacy-field', @reason, @now)
            """;
        command.Parameters.AddWithValue("@source_table", sourceTable);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@reason", $"legacy-{sourceTable}-corrupt-record");
        command.Parameters.AddWithValue("@now", _clock.UtcNow.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    internal async Task InsertLegacyDownloadedRowAsync(string id)
    {
        using var connection = await OpenConnectionAsync(readOnly: false).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO downloaded
                (id, max_speed_display, finished_timestamp, finished_time)
            VALUES (@id, '1 MiB/s', 1, 'legacy-finished')
            """;
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    internal static void SetLegacyUserVersionCommandText(SqliteCommand command, int version)
    {
        command.Parameters.Clear();
        switch (version)
        {
            case 1:
                command.CommandText = "PRAGMA user_version = 1";
                break;
            case 2:
                command.CommandText = "PRAGMA user_version = 2";
                break;
            case 3:
                command.CommandText = "PRAGMA user_version = 3";
                break;
            case 4:
                command.CommandText = "PRAGMA user_version = 4";
                break;
            case 5:
                command.CommandText = "PRAGMA user_version = 5";
                break;
            case 6:
                command.CommandText = "PRAGMA user_version = 6";
                break;
            case 7:
                command.CommandText = "PRAGMA user_version = 7";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(version));
        }
    }

    internal async Task SetLegacyDownloadStatusAsync(int status)
    {
        using var connection = await OpenConnectionAsync(readOnly: false).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE downloading SET download_status = @status";
        command.Parameters.AddWithValue("@status", status);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    internal async Task InsertOrphanedLegacyDownloadingRecordAsync()
    {
        using var connection = await OpenConnectionAsync(readOnly: false).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys = OFF;
            INSERT INTO downloading
                (id, download_files, downloaded_files, play_stream_type, download_status,
                 progress, max_speed)
            VALUES
                ('orphaned-download', '{}', '[]', 0, 0, 0, 0)
            """;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    internal async Task CreateLegacyDatabaseAsync()
    {
        Directory.CreateDirectory(_directory);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "download.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE download_base (
                id TEXT PRIMARY KEY, need_download_content TEXT NOT NULL DEFAULT '{}',
                bvid TEXT NOT NULL DEFAULT '', avid INTEGER NOT NULL DEFAULT 0,
                cid INTEGER NOT NULL DEFAULT 0, episode_id INTEGER NOT NULL DEFAULT 0,
                cover_url TEXT NOT NULL DEFAULT '', page_cover_url TEXT NOT NULL DEFAULT '',
                zone_id INTEGER NOT NULL DEFAULT 0, [order] INTEGER NOT NULL DEFAULT 0,
                main_title TEXT NOT NULL DEFAULT '', name TEXT NOT NULL DEFAULT '',
                duration TEXT NOT NULL DEFAULT '', video_codec_name TEXT NOT NULL DEFAULT '',
                resolution TEXT NOT NULL DEFAULT '{}', audio_codec TEXT,
                file_path TEXT NOT NULL DEFAULT '', file_size TEXT, page INTEGER NOT NULL DEFAULT 1
            );
            CREATE TABLE downloading (
                id TEXT PRIMARY KEY REFERENCES download_base(id) ON DELETE CASCADE, gid TEXT,
                download_files TEXT NOT NULL DEFAULT '{}', downloaded_files TEXT NOT NULL DEFAULT '[]',
                play_stream_type INTEGER NOT NULL DEFAULT 0, download_status INTEGER NOT NULL DEFAULT 0,
                download_content TEXT, download_status_title TEXT, progress REAL NOT NULL DEFAULT 0,
                downloading_file_size TEXT, max_speed INTEGER NOT NULL DEFAULT 0, speed_display TEXT
            );
            CREATE TABLE downloaded (
                id TEXT PRIMARY KEY REFERENCES download_base(id) ON DELETE CASCADE,
                max_speed_display TEXT, finished_timestamp INTEGER NOT NULL DEFAULT 0,
                finished_time TEXT NOT NULL DEFAULT ''
            );
            INSERT INTO download_base
                (id, need_download_content, bvid, avid, cid, main_title, name, resolution,
                 audio_codec, file_path)
            VALUES
                ('legacy-resume', '{"video":true}', 'BV1LEGACY', 1, 2, 'Legacy', 'Resume',
                 '{"Name":"1080P","Id":80}', '{"Name":"192K","Id":30280}', 'legacy-output');
            INSERT INTO downloading
                (id, gid, download_files, downloaded_files, play_stream_type, download_status,
                 progress, max_speed)
            VALUES
                ('legacy-resume', 'aria-gid', '{"video":"video.m4s"}', '["cover"]', 1, 3, 42.5, 4000000);
            """;
        await schema.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    internal async Task CreateIncompatibleLegacyDatabaseAsync()
    {
        Directory.CreateDirectory(_directory);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "download.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE download_base (
                id TEXT PRIMARY KEY, need_download_content TEXT NOT NULL DEFAULT '{}',
                bvid TEXT NOT NULL DEFAULT '', avid INTEGER NOT NULL DEFAULT 0,
                cid INTEGER NOT NULL DEFAULT 0, episode_id INTEGER NOT NULL DEFAULT 0,
                cover_url TEXT NOT NULL DEFAULT '', page_cover_url TEXT NOT NULL DEFAULT '',
                zone_id INTEGER NOT NULL DEFAULT 0, [order] INTEGER NOT NULL DEFAULT 0,
                main_title TEXT NOT NULL DEFAULT '', name TEXT NOT NULL DEFAULT '',
                duration TEXT NOT NULL DEFAULT '', video_codec_name TEXT NOT NULL DEFAULT '',
                resolution TEXT NOT NULL DEFAULT '{}', audio_codec TEXT,
                file_path TEXT NOT NULL DEFAULT '', file_size TEXT, page INTEGER NOT NULL DEFAULT 1
            );
            CREATE TABLE downloading (id TEXT PRIMARY KEY);
            """;
        await schema.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
    }
}
