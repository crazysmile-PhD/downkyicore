using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;
using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Downloads;

internal static class DownloadTaskSqlWriter
{
    public static async Task InsertBaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DownloadTask task,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO download_base
                (id, need_download_content, bvid, avid, cid, episode_id, cover_url, page_cover_url,
                 zone_id, [order], main_title, name, duration, video_codec_name, resolution,
                 audio_codec, file_path, output_reservation_key, file_size, page, version,
                 created_at_utc, updated_at_utc)
            VALUES
                (@id, @need_download_content, @bvid, @avid, @cid, @episode_id, @cover_url, @page_cover_url,
                 @zone_id, @order, @main_title, @name, @duration, @video_codec_name, @resolution,
                 @audio_codec, @file_path, @output_reservation_key, @file_size, @page, @version,
                 @created_at_utc, @updated_at_utc)
            """;
        BindBase(command, task);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<int> UpdateBaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DownloadTask task,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE download_base SET
                need_download_content = @need_download_content,
                bvid = @bvid, avid = @avid, cid = @cid, episode_id = @episode_id,
                cover_url = @cover_url, page_cover_url = @page_cover_url,
                zone_id = @zone_id, [order] = @order, main_title = @main_title, name = @name,
                duration = @duration, video_codec_name = @video_codec_name, resolution = @resolution,
                audio_codec = @audio_codec, file_path = @file_path,
                output_reservation_key = CASE
                    WHEN @output_reservation_key IS NULL THEN NULL
                    WHEN output_reservation_key IS NULL THEN NULL
                    ELSE @output_reservation_key
                END,
                file_size = @file_size, page = @page,
                version = @version, created_at_utc = @created_at_utc, updated_at_utc = @updated_at_utc
            WHERE id = @id AND version = @expected_version
            """;
        BindBase(command, task);
        command.Parameters.AddWithValue("@expected_version", expectedVersion);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task WriteStateRowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DownloadTask task,
        CancellationToken cancellationToken)
    {
        if (task.Phase == DownloadPhase.Completed)
        {
            await DeleteStateRowAsync(connection, transaction, false, task.Id, cancellationToken)
                .ConfigureAwait(false);
            await UpsertDownloadedAsync(connection, transaction, task, cancellationToken).ConfigureAwait(false);
            return;
        }

        await DeleteStateRowAsync(connection, transaction, true, task.Id, cancellationToken)
            .ConfigureAwait(false);
        await UpsertDownloadingAsync(connection, transaction, task, cancellationToken).ConfigureAwait(false);
    }

    public static void BindProgress(SqliteCommand command, DownloadProgress progress)
    {
        command.Parameters.AddWithValue("@progress", progress.Percentage);
        command.Parameters.AddWithValue("@downloaded_bytes", progress.DownloadedBytes ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@total_bytes", progress.TotalBytes ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@bytes_per_second", progress.BytesPerSecond);
        command.Parameters.AddWithValue(
            "@downloaded_size_text",
            progress.DownloadedSizeText ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@speed_text", progress.SpeedText ?? (object)DBNull.Value);
    }

    public static async Task DeleteBaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DownloadTaskId taskId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM download_base WHERE id = @id";
        command.Parameters.AddWithValue("@id", taskId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void BindBase(SqliteCommand command, DownloadTask task)
    {
        command.Parameters.AddWithValue("@id", task.Id.Value);
        command.Parameters.AddWithValue(
            "@need_download_content",
            DownloadStoreJson.WriteBooleanMap(task.Plan.RequestedAssets));
        command.Parameters.AddWithValue("@bvid", task.Metadata.Media.Bvid);
        command.Parameters.AddWithValue("@avid", task.Metadata.Media.Avid);
        command.Parameters.AddWithValue("@cid", task.Metadata.Media.Cid);
        command.Parameters.AddWithValue("@episode_id", task.Metadata.Media.EpisodeId);
        command.Parameters.AddWithValue("@cover_url", task.Metadata.CoverAddress);
        command.Parameters.AddWithValue("@page_cover_url", task.Metadata.PageCoverAddress);
        command.Parameters.AddWithValue("@zone_id", task.Metadata.ZoneId);
        command.Parameters.AddWithValue("@order", task.Metadata.Media.Order);
        command.Parameters.AddWithValue("@main_title", task.Metadata.MainTitle);
        command.Parameters.AddWithValue("@name", task.Metadata.Name);
        command.Parameters.AddWithValue("@duration", task.Metadata.DurationText);
        command.Parameters.AddWithValue("@video_codec_name", task.Metadata.VideoCodecName);
        command.Parameters.AddWithValue("@resolution", DownloadStoreJson.WriteQuality(task.Metadata.Resolution));
        command.Parameters.AddWithValue("@audio_codec", DownloadStoreJson.WriteQuality(task.Metadata.AudioCodec));
        command.Parameters.AddWithValue("@file_path", task.Output.BasePath);
        command.Parameters.AddWithValue(
            "@output_reservation_key",
            task.Phase is DownloadPhase.Completed or DownloadPhase.Deleted
                ? DBNull.Value
                : DownloadOutputPathKey.Create(
                    task.Output.BasePath,
                    DownloadOutputPathKey.UsesCaseInsensitiveComparison));
        command.Parameters.AddWithValue("@file_size", task.Output.FileSizeText ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@page", task.Metadata.Media.Page);
        command.Parameters.AddWithValue("@version", task.Version);
        command.Parameters.AddWithValue("@created_at_utc", task.CreatedAtUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("@updated_at_utc", task.UpdatedAtUtc.ToUnixTimeMilliseconds());
    }

    private static async Task UpsertDownloadingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DownloadTask task,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO downloading
                (id, gid, download_files, downloaded_files, play_stream_type, download_status,
                 download_content, download_status_title, progress, downloading_file_size,
                 max_speed, speed_display, phase, failure_code, failure_message, failure_transient,
                 downloaded_bytes, total_bytes, bytes_per_second)
            VALUES
                (@id, @gid, @download_files, @downloaded_files, @play_stream_type, @download_status,
                 @download_content, @download_status_title, @progress, @downloaded_size_text,
                 @max_speed, @speed_text, @phase, @failure_code, @failure_message, @failure_transient,
                 @downloaded_bytes, @total_bytes, @bytes_per_second)
            ON CONFLICT(id) DO UPDATE SET
                gid = excluded.gid,
                download_files = excluded.download_files,
                downloaded_files = excluded.downloaded_files,
                play_stream_type = excluded.play_stream_type,
                download_status = excluded.download_status,
                download_content = excluded.download_content,
                download_status_title = excluded.download_status_title,
                progress = excluded.progress,
                downloading_file_size = excluded.downloading_file_size,
                max_speed = excluded.max_speed,
                speed_display = excluded.speed_display,
                phase = excluded.phase,
                failure_code = excluded.failure_code,
                failure_message = excluded.failure_message,
                failure_transient = excluded.failure_transient,
                downloaded_bytes = excluded.downloaded_bytes,
                total_bytes = excluded.total_bytes,
                bytes_per_second = excluded.bytes_per_second
            """;
        command.Parameters.AddWithValue("@id", task.Id.Value);
        command.Parameters.AddWithValue("@gid", task.Transfer.BackendIdentity ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@download_files", DownloadStoreJson.WriteStringMap(task.Plan.TransferFiles));
        command.Parameters.AddWithValue(
            "@downloaded_files",
            DownloadStoreJson.WriteStringList(task.Transfer.CompletedFileKeys));
        command.Parameters.AddWithValue("@play_stream_type", task.Plan.StreamType);
        command.Parameters.AddWithValue("@download_status", ToLegacyStatus(task.Phase));
        command.Parameters.AddWithValue("@download_content", task.Transfer.ActiveContent ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@download_status_title", task.Transfer.StatusText ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@max_speed", task.Transfer.MaximumBytesPerSecond);
        command.Parameters.AddWithValue("@phase", (int)task.Phase);
        command.Parameters.AddWithValue("@failure_code", task.Failure?.Code ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@failure_message", task.Failure?.Message ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "@failure_transient",
            task.Failure == null ? DBNull.Value : task.Failure.IsTransient ? 1 : 0);
        BindProgress(command, task.Progress);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertDownloadedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DownloadTask task,
        CancellationToken cancellationToken)
    {
        var completion = task.Completion
            ?? throw new InvalidOperationException("Completed download has no completion details.");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO downloaded (id, max_speed_display, finished_timestamp, finished_time)
            VALUES (@id, @max_speed_display, @finished_timestamp, @finished_time)
            ON CONFLICT(id) DO UPDATE SET
                max_speed_display = excluded.max_speed_display,
                finished_timestamp = excluded.finished_timestamp,
                finished_time = excluded.finished_time
            """;
        command.Parameters.AddWithValue("@id", task.Id.Value);
        command.Parameters.AddWithValue("@max_speed_display", completion.MaximumSpeedText ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@finished_timestamp", completion.FinishedTimestamp);
        command.Parameters.AddWithValue("@finished_time", completion.FinishedTimeText);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteStateRowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        bool completed,
        DownloadTaskId taskId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (completed)
        {
            command.CommandText = "DELETE FROM downloaded WHERE id = @id";
        }
        else
        {
            command.CommandText = "DELETE FROM downloading WHERE id = @id";
        }

        command.Parameters.AddWithValue("@id", taskId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static int ToLegacyStatus(DownloadPhase phase)
    {
        return phase switch
        {
            DownloadPhase.Queued => 1,
            DownloadPhase.Pausing => 2,
            DownloadPhase.Paused => 3,
            DownloadPhase.Downloading => 4,
            DownloadPhase.Completed => 5,
            DownloadPhase.Failed => 6,
            DownloadPhase.Canceled => 3,
            DownloadPhase.Deleted => 3,
            _ => 1
        };
    }
}
