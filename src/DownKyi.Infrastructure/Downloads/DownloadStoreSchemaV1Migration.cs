using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Downloads;

internal static class DownloadStoreSchemaV1Migration
{
    public static async Task ApplyAsync(
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

        await DownloadStoreSchemaLifecycle
            .RecordMigrationAsync(connection, transaction, 1, appliedAtUtc, cancellationToken)
            .ConfigureAwait(false);
    }
}
