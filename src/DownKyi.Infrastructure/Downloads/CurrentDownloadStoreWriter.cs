using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Downloads;

internal static class CurrentDownloadStoreWriter
{
    public static async Task CreateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                CREATE TABLE download_base (
                    id                     TEXT PRIMARY KEY,
                    need_download_content  TEXT NOT NULL DEFAULT '{}',
                    bvid                   TEXT NOT NULL DEFAULT '',
                    avid                   INTEGER NOT NULL DEFAULT 0,
                    cid                    INTEGER NOT NULL DEFAULT 0,
                    episode_id             INTEGER NOT NULL DEFAULT 0,
                    cover_url              TEXT NOT NULL DEFAULT '',
                    page_cover_url         TEXT NOT NULL DEFAULT '',
                    zone_id                INTEGER NOT NULL DEFAULT 0,
                    [order]                INTEGER NOT NULL DEFAULT 0,
                    main_title             TEXT NOT NULL DEFAULT '',
                    name                   TEXT NOT NULL DEFAULT '',
                    duration               TEXT NOT NULL DEFAULT '',
                    video_codec_name       TEXT NOT NULL DEFAULT '',
                    resolution             TEXT NOT NULL DEFAULT '{}',
                    audio_codec            TEXT,
                    file_path              TEXT NOT NULL DEFAULT '',
                    file_size              TEXT,
                    page                   INTEGER NOT NULL DEFAULT 1,
                    version                INTEGER NOT NULL DEFAULT 0,
                    created_at_utc         INTEGER NOT NULL DEFAULT 0,
                    updated_at_utc         INTEGER NOT NULL DEFAULT 0,
                    output_reservation_key TEXT,
                    nfo_request            TEXT,
                    published_artifacts    TEXT NOT NULL DEFAULT '{}',
                    staging_token          TEXT NOT NULL DEFAULT '',
                    publishing_key         TEXT,
                    publishing_file_name   TEXT,
                    publishing_length      INTEGER,
                    publishing_sha256      TEXT
                );

                CREATE TABLE downloading (
                    id                     TEXT PRIMARY KEY REFERENCES download_base(id) ON DELETE CASCADE,
                    gid                    TEXT,
                    download_files         TEXT NOT NULL DEFAULT '{}',
                    downloaded_files       TEXT NOT NULL DEFAULT '[]',
                    play_stream_type       INTEGER NOT NULL DEFAULT 0,
                    download_status        INTEGER NOT NULL DEFAULT 0,
                    download_content       TEXT,
                    download_status_title TEXT,
                    progress               REAL NOT NULL DEFAULT 0,
                    downloading_file_size  TEXT,
                    max_speed              INTEGER NOT NULL DEFAULT 0,
                    speed_display          TEXT,
                    phase                  INTEGER,
                    failure_code           TEXT,
                    failure_message        TEXT,
                    failure_transient      INTEGER,
                    downloaded_bytes       INTEGER,
                    total_bytes            INTEGER,
                    bytes_per_second       INTEGER NOT NULL DEFAULT 0,
                    is_legacy_transfer     INTEGER NOT NULL DEFAULT 1
                        CHECK(is_legacy_transfer IN (0, 1))
                );

                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await CreateHistoryTableAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await AlignTablesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await EnsureIndexesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
    }

    public static async Task ProjectAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DownloadStoreShape shape,
        CancellationToken cancellationToken)
    {
        await AlignTablesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await AddMissingColumnsAsync(connection, transaction, shape, cancellationToken)
            .ConfigureAwait(false);
        if (!shape.HasTable("download_history"))
        {
            await CreateHistoryTableAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
        }
        if (shape.HasTable("downloaded"))
        {
            await ProjectHistoryAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
        }
        await EnsureIndexesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
    }

    private static async Task CreateHistoryTableAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE download_history (
                id                    TEXT PRIMARY KEY,
                cid                   INTEGER NOT NULL DEFAULT 0,
                zone_id               INTEGER NOT NULL DEFAULT 0,
                [order]               INTEGER NOT NULL DEFAULT 0,
                main_title            TEXT NOT NULL DEFAULT '',
                name                  TEXT NOT NULL DEFAULT '',
                duration              TEXT NOT NULL DEFAULT '',
                video_codec_name      TEXT NOT NULL DEFAULT '',
                resolution            TEXT NOT NULL DEFAULT '{}',
                audio_codec           TEXT,
                file_size             TEXT,
                published_artifacts   TEXT NOT NULL DEFAULT '{}',
                finished_timestamp    INTEGER NOT NULL DEFAULT 0,
                finished_time         TEXT NOT NULL DEFAULT '',
                max_speed_display     TEXT,
                requested_content    TEXT
            )
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ProjectHistoryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM download_quarantine
            WHERE source_table = 'downloaded'
              AND record_id IN (SELECT id FROM downloading);

            DELETE FROM downloaded
            WHERE id IN (SELECT id FROM downloading);

            INSERT OR IGNORE INTO download_history
                (id, cid, zone_id, [order], main_title, name, duration,
                 video_codec_name, resolution, audio_codec, file_size,
                 published_artifacts, finished_timestamp, finished_time,
                 max_speed_display)
            SELECT
                db.id, db.cid, db.zone_id, db.[order], db.main_title, db.name,
                db.duration, db.video_codec_name, db.resolution, db.audio_codec,
                db.file_size, db.published_artifacts, d.finished_timestamp,
                d.finished_time, d.max_speed_display
            FROM download_base db
            INNER JOIN downloaded d ON d.id = db.id;

            UPDATE download_quarantine
            SET source_table = 'download_history'
            WHERE source_table = 'downloaded'
              AND record_id IN (SELECT id FROM downloaded);

            DELETE FROM download_base
            WHERE id IN (SELECT id FROM downloaded);

            DROP TABLE downloaded;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task AlignTablesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS download_quarantine (
                quarantine_id      INTEGER PRIMARY KEY AUTOINCREMENT,
                source_table       TEXT NOT NULL,
                record_id          TEXT NOT NULL,
                field_name         TEXT NOT NULL,
                reason             TEXT NOT NULL,
                quarantined_at_utc INTEGER NOT NULL,
                UNIQUE(source_table, record_id)
            );

            DROP TABLE IF EXISTS download_schema_migrations;
            DROP TABLE IF EXISTS download_upgrade_admission_gate;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task AddMissingColumnsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DownloadStoreShape shape,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (!shape.HasColumn("download_base", "version"))
        {
            command.CommandText =
                "ALTER TABLE download_base ADD COLUMN version INTEGER NOT NULL DEFAULT 0";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!shape.HasColumn("download_base", "created_at_utc"))
        {
            command.CommandText =
                "ALTER TABLE download_base ADD COLUMN created_at_utc INTEGER NOT NULL DEFAULT 0";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!shape.HasColumn("download_base", "updated_at_utc"))
        {
            command.CommandText =
                "ALTER TABLE download_base ADD COLUMN updated_at_utc INTEGER NOT NULL DEFAULT 0";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!shape.HasColumn("download_base", "output_reservation_key"))
        {
            command.CommandText =
                "ALTER TABLE download_base ADD COLUMN output_reservation_key TEXT";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!shape.HasColumn("download_base", "nfo_request"))
        {
            command.CommandText = "ALTER TABLE download_base ADD COLUMN nfo_request TEXT";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!shape.HasColumn("download_base", "published_artifacts"))
        {
            command.CommandText =
                "ALTER TABLE download_base ADD COLUMN published_artifacts TEXT NOT NULL DEFAULT '{}'";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!shape.HasColumn("download_base", "staging_token"))
        {
            command.CommandText =
                "ALTER TABLE download_base ADD COLUMN staging_token TEXT NOT NULL DEFAULT ''";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        command.CommandText = """
            UPDATE download_base
            SET staging_token = lower(hex(randomblob(16)))
            WHERE staging_token IS NULL
               OR length(staging_token) <> 32
               OR staging_token GLOB '*[^0-9A-Fa-f]*'
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        if (!shape.HasColumn("download_base", "publishing_key"))
        {
            command.CommandText = "ALTER TABLE download_base ADD COLUMN publishing_key TEXT";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!shape.HasColumn("download_base", "publishing_file_name"))
        {
            command.CommandText =
                "ALTER TABLE download_base ADD COLUMN publishing_file_name TEXT";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!shape.HasColumn("download_base", "publishing_length"))
        {
            command.CommandText =
                "ALTER TABLE download_base ADD COLUMN publishing_length INTEGER";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!shape.HasColumn("download_base", "publishing_sha256"))
        {
            command.CommandText = "ALTER TABLE download_base ADD COLUMN publishing_sha256 TEXT";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!shape.HasColumn("downloading", "phase"))
        {
            command.CommandText = "ALTER TABLE downloading ADD COLUMN phase INTEGER";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!shape.HasColumn("downloading", "failure_code"))
        {
            command.CommandText = "ALTER TABLE downloading ADD COLUMN failure_code TEXT";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!shape.HasColumn("downloading", "failure_message"))
        {
            command.CommandText = "ALTER TABLE downloading ADD COLUMN failure_message TEXT";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!shape.HasColumn("downloading", "failure_transient"))
        {
            command.CommandText = "ALTER TABLE downloading ADD COLUMN failure_transient INTEGER";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!shape.HasColumn("downloading", "downloaded_bytes"))
        {
            command.CommandText = "ALTER TABLE downloading ADD COLUMN downloaded_bytes INTEGER";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!shape.HasColumn("downloading", "total_bytes"))
        {
            command.CommandText = "ALTER TABLE downloading ADD COLUMN total_bytes INTEGER";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!shape.HasColumn("downloading", "bytes_per_second"))
        {
            command.CommandText =
                "ALTER TABLE downloading ADD COLUMN bytes_per_second INTEGER NOT NULL DEFAULT 0";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!shape.HasColumn("downloading", "is_legacy_transfer"))
        {
            command.CommandText = """
                ALTER TABLE downloading
                ADD COLUMN is_legacy_transfer INTEGER NOT NULL DEFAULT 1
                    CHECK(is_legacy_transfer IN (0, 1))
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (shape.HasTable("download_history")
            && !shape.HasColumn("download_history", "requested_content"))
        {
            command.CommandText =
                "ALTER TABLE download_history ADD COLUMN requested_content TEXT";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal static async Task EnsureIndexesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE INDEX IF NOT EXISTS ix_downloading_status
                ON downloading(download_status);
            CREATE INDEX IF NOT EXISTS ix_download_history_finished_timestamp
                ON download_history(finished_timestamp DESC, id DESC);
            CREATE INDEX IF NOT EXISTS ix_download_base_main_title_order
                ON download_base(main_title, [order]);
            CREATE INDEX IF NOT EXISTS ix_download_base_cid
                ON download_base(cid);
            CREATE INDEX IF NOT EXISTS ix_download_history_cid
                ON download_history(cid);
            CREATE INDEX IF NOT EXISTS ix_download_base_file_path
                ON download_base(file_path);
            CREATE INDEX IF NOT EXISTS ix_download_base_file_path_nocase
                ON download_base(file_path COLLATE NOCASE);
            DROP INDEX IF EXISTS ux_download_base_output_reservation;
            CREATE INDEX IF NOT EXISTS ix_download_base_output_reservation
                ON download_base(output_reservation_key)
                WHERE output_reservation_key IS NOT NULL;
            CREATE INDEX IF NOT EXISTS ix_download_base_unkeyed_output
                ON download_base(file_path)
                WHERE output_reservation_key IS NULL;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
