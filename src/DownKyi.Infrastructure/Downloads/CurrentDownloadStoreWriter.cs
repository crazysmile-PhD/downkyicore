using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Downloads;

internal static class CurrentDownloadStoreWriter
{
    private const string LegacyPathQuarantineReason = "legacy-output-path-unverified";

    public static async Task CreateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset appliedAtUtc,
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
                    bytes_per_second       INTEGER NOT NULL DEFAULT 0
                );

                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await CreateHistoryTableAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await EnsureSupportTablesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await EnsureIndexesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await CompleteAsync(connection, transaction, appliedAtUtc, cancellationToken).ConfigureAwait(false);
    }

    public static async Task UpgradeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LegacyDownloadStoreFormat format,
        LegacyDownloadNormalizationPlan plan,
        DateTimeOffset appliedAtUtc,
        CancellationToken cancellationToken)
    {
        await EnsureSupportTablesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await AddMissingColumnsAsync(connection, transaction, format, cancellationToken)
            .ConfigureAwait(false);
        await CreateHistoryTableAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await ProjectLegacyHistoryAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await ApplyPhaseUpdatesAsync(connection, transaction, plan.PhaseUpdates, cancellationToken)
            .ConfigureAwait(false);
        await ApplyReservationUpdatesAsync(connection, transaction, plan.ReservationUpdates, cancellationToken)
            .ConfigureAwait(false);
        await ApplyStagingTokenUpdatesAsync(connection, transaction, plan.StagingTokenUpdates, cancellationToken)
            .ConfigureAwait(false);
        await ApplyQuarantineAsync(
            connection,
            transaction,
            plan.QuarantineRecordIds,
            appliedAtUtc,
            cancellationToken).ConfigureAwait(false);
        await EnsureIndexesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await CompleteAsync(connection, transaction, appliedAtUtc, cancellationToken).ConfigureAwait(false);
    }

    public static async Task RecoverCurrentWithLegacyHistoryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LegacyDownloadNormalizationPlan plan,
        DateTimeOffset appliedAtUtc,
        CancellationToken cancellationToken)
    {
        await ProjectLegacyHistoryAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await ApplyStagingTokenUpdatesAsync(
            connection,
            transaction,
            plan.StagingTokenUpdates,
            cancellationToken).ConfigureAwait(false);
        await EnsureIndexesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await CompleteAsync(connection, transaction, appliedAtUtc, cancellationToken).ConfigureAwait(false);
    }

    public static async Task UpgradeHistoryContentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset appliedAtUtc,
        CancellationToken cancellationToken)
    {
        await AddHistoryContentColumnAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        await CompleteAsync(connection, transaction, appliedAtUtc, cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task AddHistoryContentColumnAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "ALTER TABLE download_history ADD COLUMN requested_content TEXT";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    private static async Task ProjectLegacyHistoryAsync(
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

    private static async Task EnsureSupportTablesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS download_schema_migrations (
                version        INTEGER PRIMARY KEY,
                applied_at_utc INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS download_quarantine (
                quarantine_id      INTEGER PRIMARY KEY AUTOINCREMENT,
                source_table       TEXT NOT NULL,
                record_id          TEXT NOT NULL,
                field_name         TEXT NOT NULL,
                reason             TEXT NOT NULL,
                quarantined_at_utc INTEGER NOT NULL,
                UNIQUE(source_table, record_id)
            );

            CREATE TABLE IF NOT EXISTS download_upgrade_admission_gate (
                singleton_id INTEGER PRIMARY KEY CHECK(singleton_id = 1),
                remote_stopped_confirmed INTEGER NOT NULL DEFAULT 0
                    CHECK(remote_stopped_confirmed IN (0, 1))
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task AddMissingColumnsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LegacyDownloadStoreFormat format,
        CancellationToken cancellationToken)
    {
        if (!format.HasStateColumns)
        {
            await AddStateColumnsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        }

        if (!format.HasReservationKey)
        {
            using var reservation = connection.CreateCommand();
            reservation.Transaction = transaction;
            reservation.CommandText = "ALTER TABLE download_base ADD COLUMN output_reservation_key TEXT";
            await reservation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!format.HasNfoRequest)
        {
            using var nfo = connection.CreateCommand();
            nfo.Transaction = transaction;
            nfo.CommandText = "ALTER TABLE download_base ADD COLUMN nfo_request TEXT";
            await nfo.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!format.HasPublishedArtifacts)
        {
            using var published = connection.CreateCommand();
            published.Transaction = transaction;
            published.CommandText =
                "ALTER TABLE download_base ADD COLUMN published_artifacts TEXT NOT NULL DEFAULT '{}'";
            await published.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!format.HasStagingToken)
        {
            using var staging = connection.CreateCommand();
            staging.Transaction = transaction;
            staging.CommandText = "ALTER TABLE download_base ADD COLUMN staging_token TEXT NOT NULL DEFAULT ''";
            await staging.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!format.HasPublishingArtifact)
        {
            using var publishing = connection.CreateCommand();
            publishing.Transaction = transaction;
            publishing.CommandText = """
                ALTER TABLE download_base ADD COLUMN publishing_key TEXT;
                ALTER TABLE download_base ADD COLUMN publishing_file_name TEXT;
                ALTER TABLE download_base ADD COLUMN publishing_length INTEGER;
                ALTER TABLE download_base ADD COLUMN publishing_sha256 TEXT;
                """;
            await publishing.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task AddStateColumnsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "ALTER TABLE download_base ADD COLUMN version INTEGER NOT NULL DEFAULT 0";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        command.CommandText = "ALTER TABLE download_base ADD COLUMN created_at_utc INTEGER NOT NULL DEFAULT 0";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        command.CommandText = "ALTER TABLE download_base ADD COLUMN updated_at_utc INTEGER NOT NULL DEFAULT 0";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        command.CommandText = "ALTER TABLE downloading ADD COLUMN phase INTEGER";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        command.CommandText = "ALTER TABLE downloading ADD COLUMN failure_code TEXT";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        command.CommandText = "ALTER TABLE downloading ADD COLUMN failure_message TEXT";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        command.CommandText = "ALTER TABLE downloading ADD COLUMN failure_transient INTEGER";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        command.CommandText = "ALTER TABLE downloading ADD COLUMN downloaded_bytes INTEGER";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        command.CommandText = "ALTER TABLE downloading ADD COLUMN total_bytes INTEGER";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        command.CommandText =
            "ALTER TABLE downloading ADD COLUMN bytes_per_second INTEGER NOT NULL DEFAULT 0";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyPhaseUpdatesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<LegacyPhaseUpdate> updates,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE downloading SET phase = @phase WHERE id = @id AND phase IS NULL";
        var phase = command.Parameters.Add("@phase", SqliteType.Integer);
        var id = command.Parameters.Add("@id", SqliteType.Text);
        foreach (var update in updates)
        {
            phase.Value = update.Phase;
            id.Value = update.Id;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ApplyReservationUpdatesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<LegacyReservationUpdate> updates,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE download_base SET output_reservation_key = @key WHERE id = @id";
        var key = command.Parameters.Add("@key", SqliteType.Text);
        var id = command.Parameters.Add("@id", SqliteType.Text);
        foreach (var update in updates)
        {
            key.Value = update.ReservationKey;
            id.Value = update.Id;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ApplyStagingTokenUpdatesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<LegacyStagingTokenUpdate> updates,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE download_base SET staging_token = @token WHERE id = @id";
        var token = command.Parameters.Add("@token", SqliteType.Text);
        var id = command.Parameters.Add("@id", SqliteType.Text);
        foreach (var update in updates)
        {
            token.Value = update.StagingToken;
            id.Value = update.Id;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ApplyQuarantineAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<string> recordIds,
        DateTimeOffset appliedAtUtc,
        CancellationToken cancellationToken)
    {
        if (recordIds.Count == 0)
        {
            return;
        }

        using (var quarantine = connection.CreateCommand())
        {
            quarantine.Transaction = transaction;
            quarantine.CommandText = """
                INSERT INTO download_quarantine
                    (source_table, record_id, field_name, reason, quarantined_at_utc)
                VALUES ('downloading', @record_id, 'file_path', @reason, @quarantined_at_utc)
                ON CONFLICT(source_table, record_id) DO NOTHING
                """;
            var recordId = quarantine.Parameters.Add("@record_id", SqliteType.Text);
            quarantine.Parameters.AddWithValue("@reason", LegacyPathQuarantineReason);
            quarantine.Parameters.AddWithValue(
                "@quarantined_at_utc",
                appliedAtUtc.ToUnixTimeMilliseconds());
            foreach (var id in recordIds)
            {
                recordId.Value = id;
                await quarantine.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        using var gate = connection.CreateCommand();
        gate.Transaction = transaction;
        gate.CommandText = """
            INSERT INTO download_upgrade_admission_gate
                (singleton_id, remote_stopped_confirmed)
            VALUES (1, 0)
            ON CONFLICT(singleton_id) DO NOTHING
            """;
        await gate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureIndexesAsync(
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
            CREATE INDEX IF NOT EXISTS ix_download_base_file_path
                ON download_base(file_path);
            CREATE INDEX IF NOT EXISTS ix_download_base_file_path_nocase
                ON download_base(file_path COLLATE NOCASE);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_download_base_output_reservation
                ON download_base(output_reservation_key)
                WHERE output_reservation_key IS NOT NULL;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task CompleteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset appliedAtUtc,
        CancellationToken cancellationToken)
    {
        await DownloadStoreSchemaLifecycle
            .RecordMigrationAsync(
                connection,
                transaction,
                DownloadStoreSchema.CurrentVersion,
                appliedAtUtc,
                cancellationToken)
            .ConfigureAwait(false);
        await DownloadStoreSchemaLifecycle
            .SetUserVersionAsync(
                connection,
                transaction,
                DownloadStoreSchema.CurrentVersion,
                cancellationToken)
            .ConfigureAwait(false);
    }
}
