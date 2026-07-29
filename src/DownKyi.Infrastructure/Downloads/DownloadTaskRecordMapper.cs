using System.Collections.Immutable;
using DownKyi.Domain.Downloads;
using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Downloads;

internal static class DownloadTaskRecordMapper
{
    public static DownloadTask Read(SqliteDataReader reader)
    {
        try
        {
            return ReadCore(reader);
        }
        catch (DownloadRecordCorruptException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidCastException or OverflowException)
        {
            throw new DownloadRecordCorruptException(
                "record",
                "Stored download violates the download task contract.",
                exception);
        }
    }

    private static DownloadTask ReadCore(SqliteDataReader reader)
    {
        var id = new DownloadTaskId(reader.GetString(reader.GetOrdinal("id")));
        var requestedAssets = DownloadStoreJson.ReadBooleanMap(
            reader.GetString(reader.GetOrdinal("need_download_content")),
            "need_download_content");
        var transferFiles = reader.IsDBNull(reader.GetOrdinal("download_files"))
            ? ImmutableDictionary<string, string>.Empty
            : DownloadStoreJson.ReadStringMap(reader.GetString(reader.GetOrdinal("download_files")), "download_files");
        var completedFiles = reader.IsDBNull(reader.GetOrdinal("downloaded_files"))
            ? ImmutableArray<string>.Empty
            : DownloadStoreJson.ReadStringList(reader.GetString(reader.GetOrdinal("downloaded_files")), "downloaded_files");
        var metadata = new DownloadTaskMetadata(
            new DownloadMediaIdentity(
                GetString(reader, "bvid"),
                reader.GetInt64(reader.GetOrdinal("avid")),
                reader.GetInt64(reader.GetOrdinal("cid")),
                reader.GetInt64(reader.GetOrdinal("episode_id")),
                reader.GetInt32(reader.GetOrdinal("page")),
                reader.GetInt32(reader.GetOrdinal("order"))),
            GetString(reader, "main_title"),
            GetString(reader, "name"),
            GetString(reader, "duration"),
            GetString(reader, "video_codec_name"),
            DownloadStoreJson.ReadQuality(GetNullableString(reader, "resolution"), "resolution"),
            DownloadStoreJson.ReadQuality(GetNullableString(reader, "audio_codec"), "audio_codec"),
            GetString(reader, "cover_url"),
            GetString(reader, "page_cover_url"),
            reader.GetInt32(reader.GetOrdinal("zone_id")));
        var plan = new DownloadPlan(
            requestedAssets,
            transferFiles,
            reader.IsDBNull(reader.GetOrdinal("play_stream_type"))
                ? 0
                : reader.GetInt32(reader.GetOrdinal("play_stream_type")));
        var progress = new DownloadProgress(
            reader.IsDBNull(reader.GetOrdinal("progress")) ? 0 : reader.GetDouble(reader.GetOrdinal("progress")),
            GetNullableInt64(reader, "downloaded_bytes"),
            GetNullableInt64(reader, "total_bytes"),
            reader.IsDBNull(reader.GetOrdinal("bytes_per_second"))
                ? 0
                : reader.GetInt64(reader.GetOrdinal("bytes_per_second")),
            GetNullableString(reader, "downloading_file_size"),
            GetNullableString(reader, "speed_display"));
        var transfer = new DownloadTransferState(
            GetNullableString(reader, "gid"),
            completedFiles,
            GetNullableString(reader, "download_content"),
            GetNullableString(reader, "download_status_title"),
            reader.IsDBNull(reader.GetOrdinal("max_speed")) ? 0 : reader.GetInt64(reader.GetOrdinal("max_speed")));
        var isCompleted = !reader.IsDBNull(reader.GetOrdinal("finished_timestamp"));
        var phase = isCompleted ? DownloadPhase.Completed : ReadPhase(reader);
        DownloadFailure? failure = null;
        if (phase == DownloadPhase.Failed)
        {
            failure = new DownloadFailure(
                GetNullableString(reader, "failure_code") ?? "download.legacy.failed",
                GetNullableString(reader, "failure_message")
                    ?? GetNullableString(reader, "download_status_title")
                    ?? "Stored download failed.",
                !reader.IsDBNull(reader.GetOrdinal("failure_transient"))
                    && reader.GetBoolean(reader.GetOrdinal("failure_transient")));
        }

        DownloadCompletion? completion = null;
        if (isCompleted)
        {
            completion = new DownloadCompletion(
                reader.GetInt64(reader.GetOrdinal("finished_timestamp")),
                GetString(reader, "finished_time"),
                GetNullableString(reader, "max_speed_display"));
        }

        var createdAt = ReadTimestamp(reader, "created_at_utc");
        var updatedAt = ReadTimestamp(reader, "updated_at_utc");
        if (updatedAt < createdAt)
        {
            throw new DownloadRecordCorruptException("updated_at_utc", "Updated timestamp precedes creation.");
        }

        return DownloadTask.Restore(
            id,
            metadata,
            plan,
            new DownloadOutput(GetString(reader, "file_path"), GetNullableString(reader, "file_size")),
            phase,
            progress,
            transfer,
            failure,
            completion,
            reader.GetInt64(reader.GetOrdinal("version")),
            createdAt,
            updatedAt);
    }

    private static DownloadPhase ReadPhase(SqliteDataReader reader)
    {
        var value = reader.IsDBNull(reader.GetOrdinal("phase"))
            ? MapLegacyStatus(reader.GetInt32(reader.GetOrdinal("download_status")))
            : (DownloadPhase)reader.GetInt32(reader.GetOrdinal("phase"));
        if (!Enum.IsDefined(value) || value is DownloadPhase.Completed or DownloadPhase.Deleted)
        {
            throw new DownloadRecordCorruptException("phase", "Stored download phase is invalid for an unfinished row.");
        }

        return value;
    }

    private static DownloadPhase MapLegacyStatus(int status)
    {
        return status switch
        {
            2 => DownloadPhase.Pausing,
            3 => DownloadPhase.Paused,
            4 => DownloadPhase.Downloading,
            5 => DownloadPhase.Queued,
            6 => DownloadPhase.Failed,
            _ => DownloadPhase.Queued
        };
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

    private static long? GetNullableInt64(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    private static DateTimeOffset ReadTimestamp(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) || reader.GetInt64(ordinal) == 0
            ? DateTimeOffset.UnixEpoch
            : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(ordinal));
    }
}
