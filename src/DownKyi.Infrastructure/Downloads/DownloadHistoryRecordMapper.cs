using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;
using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Downloads;

internal static class DownloadHistoryRecordMapper
{
    public const string SelectColumns = """
        SELECT
            h.id, h.cid, h.zone_id, h.[order], h.main_title, h.name, h.duration,
            h.video_codec_name, h.resolution, h.audio_codec, h.file_size,
            h.published_artifacts, h.finished_timestamp, h.finished_time,
            h.max_speed_display, h.requested_content
        FROM download_history h
        """;

    public static DownloadHistoryRecord Read(SqliteDataReader reader)
    {
        try
        {
            var resolution = DownloadStoreJson.ReadQuality(
                GetNullableString(reader, "resolution"),
                "resolution");
            var audioCodec = DownloadStoreJson.ReadQuality(
                GetNullableString(reader, "audio_codec"),
                "audio_codec");
            return new DownloadHistoryRecord(
                new DownloadTaskId(reader.GetString(reader.GetOrdinal("id"))),
                reader.GetInt64(reader.GetOrdinal("cid")),
                reader.GetInt32(reader.GetOrdinal("zone_id")),
                reader.GetInt32(reader.GetOrdinal("order")),
                GetString(reader, "main_title"),
                GetString(reader, "name"),
                GetString(reader, "duration"),
                GetString(reader, "video_codec_name"),
                resolution.Id,
                resolution.Name,
                audioCodec.Name,
                GetNullableString(reader, "file_size"),
                DownloadStoreJson.ReadStringMap(
                    GetString(reader, "published_artifacts"),
                    "published_artifacts"),
                reader.GetInt64(reader.GetOrdinal("finished_timestamp")),
                GetString(reader, "finished_time"),
                GetNullableString(reader, "max_speed_display"),
                GetNullableString(reader, "requested_content") is { } content
                    ? DownloadStoreJson.ReadContentSelection(content, "requested_content")
                    : null);
        }
        catch (DownloadRecordCorruptException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidCastException
            or OverflowException)
        {
            throw new DownloadRecordCorruptException(
                "record",
                "Stored history violates the download history contract.",
                exception);
        }
    }

    private static string GetString(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
    }

    private static string? GetNullableString(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }
}
