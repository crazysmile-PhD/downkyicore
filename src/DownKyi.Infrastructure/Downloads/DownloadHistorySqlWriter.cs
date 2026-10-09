using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;
using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Downloads;

internal static class DownloadHistorySqlWriter
{
    public static async Task InsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DownloadHistoryRecord history,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO download_history
                (id, cid, zone_id, [order], main_title, name, duration,
                 video_codec_name, resolution, audio_codec, file_size,
                 published_artifacts, finished_timestamp, finished_time,
                 max_speed_display, requested_content)
            VALUES
                (@id, @cid, @zone_id, @order, @main_title, @name, @duration,
                 @video_codec_name, @resolution, @audio_codec, @file_size,
                 @published_artifacts, @finished_timestamp, @finished_time,
                 @max_speed_display, @requested_content)
            """;
        Bind(command, history);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Bind(SqliteCommand command, DownloadHistoryRecord history)
    {
        command.Parameters.AddWithValue("@id", history.Id.Value);
        command.Parameters.AddWithValue("@cid", history.Cid);
        command.Parameters.AddWithValue("@zone_id", history.ZoneId);
        command.Parameters.AddWithValue("@order", history.Order);
        command.Parameters.AddWithValue("@main_title", history.MainTitle);
        command.Parameters.AddWithValue("@name", history.Name);
        command.Parameters.AddWithValue("@duration", history.DurationText);
        command.Parameters.AddWithValue("@video_codec_name", history.VideoCodecName);
        command.Parameters.AddWithValue(
            "@resolution",
            DownloadStoreJson.WriteQuality(
                new DownloadQuality(history.ResolutionId, history.ResolutionName)));
        command.Parameters.AddWithValue(
            "@audio_codec",
            DownloadStoreJson.WriteQuality(new DownloadQuality(0, history.AudioCodecName)));
        command.Parameters.AddWithValue("@file_size", history.FileSizeText ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "@published_artifacts",
            DownloadStoreJson.WriteStringMap(history.PublishedArtifacts));
        command.Parameters.AddWithValue("@finished_timestamp", history.FinishedTimestamp);
        command.Parameters.AddWithValue("@finished_time", history.FinishedTimeText);
        command.Parameters.AddWithValue(
            "@max_speed_display",
            history.MaximumSpeedText ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "@requested_content",
            history.RequestedContent is { } content
                ? DownloadStoreJson.WriteContentSelection(content)
                : DBNull.Value);
    }
}
