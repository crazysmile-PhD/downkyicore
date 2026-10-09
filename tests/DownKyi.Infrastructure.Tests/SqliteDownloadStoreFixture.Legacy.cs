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
        CopyHistoricalDatabase(3);
        foreach (var task in tasks)
        {
            await InsertHistoricalTaskAsync(task, 3).ConfigureAwait(false);
        }
    }

    internal async Task CreateVersionFourDatabaseAsync()
    {
        CopyHistoricalDatabase(4);
        await InsertHistoricalTaskAsync(
            CreatePausedTask(
                "version-four",
                "version-four-output",
                requestedContent: DownloadContentSelection.None),
            4).ConfigureAwait(false);
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
        if (version is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }

        CopyHistoricalDatabase(version);
        await InsertHistoricalTaskAsync(CreatePausedTask($"legacy-v{version}"), version)
            .ConfigureAwait(false);
    }

    internal void CopyHistoricalDatabase(int version)
    {
        if (version is < 0 or > 9)
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }

        Directory.CreateDirectory(_directory);
        var source = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            $"download-store-v{version}.db");
        File.Copy(source, Path.Combine(_directory, "download.db"));
    }

    internal async Task InsertHistoricalTaskAsync(DownloadTask task, int version)
    {
        using var connection = await OpenConnectionAsync(readOnly: false).ConfigureAwait(false);
        using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO download_base
                (id, need_download_content, bvid, avid, cid, main_title, name,
                 resolution, audio_codec, file_path, file_size)
            VALUES
                (@id, @content, @bvid, @avid, @cid, @title, @name,
                 @resolution, @audio, @path, @size)
            """;
        command.Parameters.AddWithValue("@id", task.Id.Value);
        command.Parameters.AddWithValue(
            "@content", DownloadStoreJson.WriteContentSelection(task.Plan.RequestedContent));
        command.Parameters.AddWithValue("@bvid", task.Metadata.Media.Bvid);
        command.Parameters.AddWithValue("@avid", task.Metadata.Media.Avid);
        command.Parameters.AddWithValue("@cid", task.Metadata.Media.Cid);
        command.Parameters.AddWithValue("@title", task.Metadata.MainTitle);
        command.Parameters.AddWithValue("@name", task.Metadata.Name);
        command.Parameters.AddWithValue(
            "@resolution", DownloadStoreJson.WriteQuality(task.Metadata.Resolution));
        command.Parameters.AddWithValue(
            "@audio", DownloadStoreJson.WriteQuality(task.Metadata.AudioCodec));
        command.Parameters.AddWithValue("@path", task.Output.BasePath);
        command.Parameters.AddWithValue("@size", task.Output.FileSizeText ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);

        command.Parameters.Clear();
        command.Parameters.AddWithValue("@id", task.Id.Value);
        if (version >= 7)
        {
            command.CommandText = "UPDATE download_base SET staging_token = @token WHERE id = @id";
            command.Parameters.AddWithValue("@token", task.Output.StagingToken);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
            command.Parameters.Clear();
            command.Parameters.AddWithValue("@id", task.Id.Value);
        }

        if (task.Phase == DownloadPhase.Completed)
        {
            command.CommandText = """
                INSERT INTO downloaded (id, max_speed_display, finished_timestamp, finished_time)
                VALUES (@id, @speed, @timestamp, @time)
                """;
            command.Parameters.AddWithValue(
                "@speed", task.Completion!.MaximumSpeedText ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@timestamp", task.Completion.FinishedTimestamp);
            command.Parameters.AddWithValue("@time", task.Completion.FinishedTimeText);
        }
        else
        {
            command.CommandText = """
                INSERT INTO downloading
                    (id, gid, download_files, downloaded_files, play_stream_type,
                     download_status, progress, max_speed)
                VALUES
                    (@id, @gid, @files, @completed, 1, 3, @progress, @speed)
                """;
            command.Parameters.AddWithValue("@gid", task.Transfer.BackendIdentity ?? (object)DBNull.Value);
            command.Parameters.AddWithValue(
                "@files", DownloadStoreJson.WriteStringMap(task.Plan.TransferFiles));
            command.Parameters.AddWithValue(
                "@completed", DownloadStoreJson.WriteStringList(task.Transfer.CompletedFileKeys));
            command.Parameters.AddWithValue("@progress", task.Progress.Percentage);
            command.Parameters.AddWithValue("@speed", task.Transfer.MaximumBytesPerSecond);
        }

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        if (version >= 2 && task.Phase != DownloadPhase.Completed)
        {
            command.Parameters.Clear();
            command.CommandText = "UPDATE downloading SET phase = @phase WHERE id = @id";
            command.Parameters.AddWithValue("@phase", (int)task.Phase);
            command.Parameters.AddWithValue("@id", task.Id.Value);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    internal async Task InsertVersionNineHistoryAsync()
    {
        using var connection = await OpenConnectionAsync(readOnly: false).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO download_history (id, name, finished_timestamp, finished_time)
            VALUES ('preexisting-history', 'preexisting-history', 123, 'finished')
            """;
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
        CopyHistoricalDatabase(0);
        using var connection = await OpenConnectionAsync(readOnly: false).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
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
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    internal async Task CreateIncompatibleLegacyDatabaseAsync()
    {
        CopyHistoricalDatabase(0);
        using var connection = await OpenConnectionAsync(readOnly: false).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            DROP TABLE downloading;
            DROP TABLE downloaded;
            CREATE TABLE downloading (id TEXT PRIMARY KEY);
            """;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
    }
}
