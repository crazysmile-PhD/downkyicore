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

}
