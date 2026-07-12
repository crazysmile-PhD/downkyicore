using System.Collections.Immutable;
using DownKyi.Application.Downloads;
using DownKyi.Application.Time;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Downloads;

public sealed class SqliteDownloadTaskStore : IDownloadTaskStore, IDisposable
{
    private const int MaximumHistoryPageSize = 500;
    private readonly SqliteDownloadTaskStoreOptions _options;
    private readonly IClock _clock;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private volatile bool _initialized;
    private bool _disposed;

    public SqliteDownloadTaskStore(SqliteDownloadTaskStoreOptions options, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        if (options.BusyTimeout <= TimeSpan.Zero || options.BusyTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        _options = options;
        _clock = clock;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = checked((int)Math.Ceiling(options.BusyTimeout.TotalSeconds))
        }.ToString();
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        return EnsureInitializedAsync(cancellationToken);
    }

    public async Task<OperationResult> AddAsync(DownloadTask task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.Phase == DownloadPhase.Deleted)
        {
            throw new ArgumentException("A deleted task cannot be inserted.", nameof(task));
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await InsertBaseAsync(connection, transaction, task, cancellationToken).ConfigureAwait(false);
            await WriteStateRowAsync(connection, transaction, task, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return OperationResult.Success();
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return Conflict(task.Id, "already exists");
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<OperationResult> UpdateAsync(
        DownloadTask task,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(expectedVersion, task.Version);

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var updated = await UpdateBaseAsync(
                connection,
                transaction,
                task,
                expectedVersion,
                cancellationToken).ConfigureAwait(false);
            if (updated == 0)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return Conflict(task.Id, "has changed since it was loaded");
            }

            if (task.Phase == DownloadPhase.Deleted)
            {
                await DeleteBaseAsync(connection, transaction, task.Id, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await WriteStateRowAsync(connection, transaction, task, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return OperationResult.Success();
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<OperationResult> UpdateProgressAsync(
        DownloadProgressWrite progressWrite,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progressWrite);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            using var versionCommand = connection.CreateCommand();
            versionCommand.Transaction = transaction;
            versionCommand.CommandText = """
                UPDATE download_base
                SET version = @target_version, updated_at_utc = @updated_at_utc
                WHERE id = @id AND version = @expected_version
                """;
            versionCommand.Parameters.AddWithValue("@target_version", progressWrite.TargetVersion);
            versionCommand.Parameters.AddWithValue("@updated_at_utc", progressWrite.UpdatedAtUtc.ToUnixTimeMilliseconds());
            versionCommand.Parameters.AddWithValue("@id", progressWrite.TaskId.Value);
            versionCommand.Parameters.AddWithValue("@expected_version", progressWrite.ExpectedVersion);
            var changed = await versionCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (changed == 0)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return Conflict(progressWrite.TaskId, "has changed since progress was sampled");
            }

            using var progressCommand = connection.CreateCommand();
            progressCommand.Transaction = transaction;
            progressCommand.CommandText = """
                UPDATE downloading
                SET progress = @progress,
                    downloaded_bytes = @downloaded_bytes,
                    total_bytes = @total_bytes,
                    bytes_per_second = @bytes_per_second,
                    downloading_file_size = @downloaded_size_text,
                    speed_display = @speed_text,
                    max_speed = MAX(max_speed, @bytes_per_second)
                WHERE id = @id
                """;
            BindProgress(progressCommand, progressWrite.Progress);
            progressCommand.Parameters.AddWithValue("@id", progressWrite.TaskId.Value);
            changed = await progressCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (changed == 0)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return NotFound(progressWrite.TaskId);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return OperationResult.Success();
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<DownloadTask?> FindAsync(DownloadTaskId taskId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + "\n" + """
            WHERE db.id = @id
              AND NOT EXISTS (
                  SELECT 1 FROM download_quarantine q
                  WHERE q.record_id = db.id
                    AND q.source_table = CASE WHEN d.id IS NULL THEN 'downloading' ELSE 'downloaded' END)
            """;
        command.Parameters.AddWithValue("@id", taskId.Value);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            return ReadTask(reader);
        }
        catch (DownloadRecordCorruptException exception)
        {
            var isHistory = !await reader
                .IsDBNullAsync(reader.GetOrdinal("finished_timestamp"), cancellationToken)
                .ConfigureAwait(false);
            var source = isHistory ? "downloaded" : "downloading";
            await reader.DisposeAsync().ConfigureAwait(false);
            await QuarantineAsync(connection, source, taskId.Value, exception, _clock.UtcNow, cancellationToken)
                .ConfigureAwait(false);
            return null;
        }
    }

    public async Task<IReadOnlyList<DownloadTask>> GetUnfinishedAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + "\n" + """
            WHERE dl.id IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1 FROM download_quarantine q
                  WHERE q.source_table = 'downloading' AND q.record_id = db.id)
            ORDER BY db.main_title COLLATE NOCASE, db.[order] ASC, db.id ASC
            """;
        return await ReadManyAsync(connection, command, "downloading", _clock.UtcNow, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<DownloadHistoryPage> GetHistoryPageAsync(
        DownloadHistoryCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, MaximumHistoryPageSize);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + "\n" + """
            WHERE d.id IS NOT NULL
              AND (@cursor_timestamp IS NULL
                   OR d.finished_timestamp < @cursor_timestamp
                   OR (d.finished_timestamp = @cursor_timestamp AND d.id < @cursor_id))
              AND NOT EXISTS (
                  SELECT 1 FROM download_quarantine q
                  WHERE q.source_table = 'downloaded' AND q.record_id = db.id)
            ORDER BY d.finished_timestamp DESC, d.id DESC
            LIMIT @limit
            """;
        command.Parameters.AddWithValue("@cursor_timestamp", cursor?.FinishedTimestamp ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@cursor_id", cursor?.TaskId.Value ?? string.Empty);
        command.Parameters.AddWithValue("@limit", checked(pageSize + 1));
        var items = (await ReadManyAsync(
                connection,
                command,
                "downloaded",
                _clock.UtcNow,
                cancellationToken).ConfigureAwait(false))
            .ToList();
        var hasMore = items.Count > pageSize;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }

        DownloadHistoryCursor? nextCursor = null;
        if (hasMore && items.Count > 0)
        {
            var last = items[^1];
            nextCursor = new DownloadHistoryCursor(last.Completion!.FinishedTimestamp, last.Id);
        }

        return new DownloadHistoryPage(items, nextCursor);
    }

    public async Task<OperationResult> DeleteAsync(DownloadTaskId taskId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM download_base WHERE id = @id";
        command.Parameters.AddWithValue("@id", taskId.Value);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return changed == 0 ? NotFound(taskId) : OperationResult.Success();
    }

    public async Task<OperationResult> ClearHistoryAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            const string sql = """
                DELETE FROM download_base
                WHERE id IN (SELECT id FROM downloaded)
                  AND id NOT IN (SELECT id FROM downloading);
                DELETE FROM downloaded;
                """;
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return OperationResult.Success();
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<QuarantinedDownloadRecord>> GetQuarantinedRecordsAsync(
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT quarantine_id, source_table, record_id, field_name, reason, quarantined_at_utc
            FROM download_quarantine
            ORDER BY quarantine_id ASC
            """;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var records = new List<QuarantinedDownloadRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(new QuarantinedDownloadRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5))));
        }

        return records;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _initializationGate.Dispose();
        _disposed = true;
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized)
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            var databaseExisted = File.Exists(_options.DatabasePath)
                && new FileInfo(_options.DatabasePath).Length > 0;
            Directory.CreateDirectory(Path.GetDirectoryName(_options.DatabasePath) ?? ".");
            using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await DownloadStoreSchema.InitializeAsync(
                connection,
                _options.DatabasePath,
                databaseExisted,
                _clock,
                cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA foreign_keys = ON;
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA temp_store = MEMORY;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<IReadOnlyList<DownloadTask>> ReadManyAsync(
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
                    tasks.Add(ReadTask(reader));
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

    private static DownloadTask ReadTask(SqliteDataReader reader)
    {
        try
        {
            return ReadTaskCore(reader);
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

    private static DownloadTask ReadTaskCore(SqliteDataReader reader)
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
            5 => DownloadPhase.Completed,
            6 => DownloadPhase.Failed,
            _ => DownloadPhase.Queued
        };
    }

    private static async Task InsertBaseAsync(
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
                 audio_codec, file_path, file_size, page, version, created_at_utc, updated_at_utc)
            VALUES
                (@id, @need_download_content, @bvid, @avid, @cid, @episode_id, @cover_url, @page_cover_url,
                 @zone_id, @order, @main_title, @name, @duration, @video_codec_name, @resolution,
                 @audio_codec, @file_path, @file_size, @page, @version, @created_at_utc, @updated_at_utc)
            """;
        BindBase(command, task);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> UpdateBaseAsync(
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
                audio_codec = @audio_codec, file_path = @file_path, file_size = @file_size, page = @page,
                version = @version, created_at_utc = @created_at_utc, updated_at_utc = @updated_at_utc
            WHERE id = @id AND version = @expected_version
            """;
        BindBase(command, task);
        command.Parameters.AddWithValue("@expected_version", expectedVersion);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
        command.Parameters.AddWithValue("@file_size", task.Output.FileSizeText ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@page", task.Metadata.Media.Page);
        command.Parameters.AddWithValue("@version", task.Version);
        command.Parameters.AddWithValue("@created_at_utc", task.CreatedAtUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("@updated_at_utc", task.UpdatedAtUtc.ToUnixTimeMilliseconds());
    }

    private static async Task WriteStateRowAsync(
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

    private static void BindProgress(SqliteCommand command, DownloadProgress progress)
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

    private static async Task DeleteBaseAsync(
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

    private static async Task QuarantineAsync(
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

    private static OperationResult Conflict(DownloadTaskId taskId, string reason)
    {
        return OperationResult.Failure(new OperationError(
            "download.store.conflict",
            $"Download task '{taskId.Value}' {reason}.",
            OperationErrorKind.Conflict));
    }

    private static OperationResult NotFound(DownloadTaskId taskId)
    {
        return OperationResult.Failure(new OperationError(
            "download.store.not_found",
            $"Download task '{taskId.Value}' was not found.",
            OperationErrorKind.NotFound));
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

    private const string SelectColumns = """
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
