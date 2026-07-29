using DownKyi.Domain.Downloads;
using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Downloads;

internal static class DownloadTaskSqlReader
{
    public const string SelectColumns = """
        SELECT
            db.id, db.need_download_content, db.bvid, db.avid, db.cid, db.episode_id,
            db.cover_url, db.page_cover_url, db.zone_id, db.[order], db.main_title,
            db.name, db.duration, db.video_codec_name, db.resolution, db.audio_codec,
            db.file_path, db.file_size, db.page, db.version, db.created_at_utc, db.updated_at_utc,
            dl.gid, dl.download_files, dl.downloaded_files, dl.play_stream_type,
            dl.download_status, dl.download_content, dl.download_status_title, dl.progress,
            dl.downloading_file_size, dl.max_speed, dl.speed_display, dl.phase,
            dl.failure_code, dl.failure_message, dl.failure_transient,
            dl.downloaded_bytes, dl.total_bytes, dl.bytes_per_second,
            d.max_speed_display, d.finished_timestamp, d.finished_time
        FROM download_base db
        LEFT JOIN downloading dl ON dl.id = db.id
        LEFT JOIN downloaded d ON d.id = db.id
        """;

    public static async Task<IReadOnlyList<DownloadTask>> ReadManyAsync(
        SqliteConnection connection,
        SqliteCommand command,
        string sourceTable,
        DateTimeOffset quarantinedAtUtc,
        CancellationToken cancellationToken)
    {
        var tasks = new List<DownloadTask>();
        var corrupt = new List<(string RecordId, DownloadRecordCorruptException Error)>();
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var recordId = reader.GetString(reader.GetOrdinal("id"));
                try
                {
                    tasks.Add(DownloadTaskRecordMapper.Read(reader));
                }
                catch (DownloadRecordCorruptException exception)
                {
                    corrupt.Add((recordId, exception));
                }
            }
        }

        foreach (var item in corrupt)
        {
            await QuarantineAsync(
                    connection,
                    sourceTable,
                    item.RecordId,
                    item.Error,
                    quarantinedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return tasks;
    }

    public static async Task QuarantineAsync(
        SqliteConnection connection,
        string sourceTable,
        string recordId,
        DownloadRecordCorruptException error,
        DateTimeOffset quarantinedAtUtc,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO download_quarantine
                (source_table, record_id, field_name, reason, quarantined_at_utc)
            VALUES (@source_table, @record_id, @field_name, @reason, @quarantined_at_utc)
            ON CONFLICT(source_table, record_id) DO UPDATE SET
                field_name = excluded.field_name,
                reason = excluded.reason,
                quarantined_at_utc = excluded.quarantined_at_utc
            """;
        command.Parameters.AddWithValue("@source_table", sourceTable);
        command.Parameters.AddWithValue("@record_id", recordId);
        command.Parameters.AddWithValue("@field_name", error.FieldName);
        command.Parameters.AddWithValue("@reason", error.Message);
        command.Parameters.AddWithValue("@quarantined_at_utc", quarantinedAtUtc.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
