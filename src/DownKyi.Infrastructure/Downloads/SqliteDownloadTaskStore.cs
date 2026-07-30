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
            await DownloadTaskSqlWriter
                .InsertBaseAsync(connection, transaction, task, cancellationToken)
                .ConfigureAwait(false);
            await DownloadTaskSqlWriter
                .WriteStateRowAsync(connection, transaction, task, cancellationToken)
                .ConfigureAwait(false);
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
            var updated = await DownloadTaskSqlWriter.UpdateBaseAsync(
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
                await DownloadTaskSqlWriter
                    .DeleteBaseAsync(connection, transaction, task.Id, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await DownloadTaskSqlWriter
                    .WriteStateRowAsync(connection, transaction, task, cancellationToken)
                    .ConfigureAwait(false);
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
            DownloadTaskSqlWriter.BindProgress(progressCommand, progressWrite.Progress);
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
        command.CommandText = DownloadTaskSqlReader.SelectColumns + "\n" + """
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
            return DownloadTaskRecordMapper.Read(reader);
        }
        catch (DownloadRecordCorruptException exception)
        {
            var isHistory = !await reader
                .IsDBNullAsync(reader.GetOrdinal("finished_timestamp"), cancellationToken)
                .ConfigureAwait(false);
            var source = isHistory ? "downloaded" : "downloading";
            await reader.DisposeAsync().ConfigureAwait(false);
            await DownloadTaskSqlReader
                .QuarantineAsync(connection, source, taskId.Value, exception, _clock.UtcNow, cancellationToken)
                .ConfigureAwait(false);
            return null;
        }
    }

    public async Task<IReadOnlyList<DownloadTask>> GetUnfinishedAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = DownloadTaskSqlReader.SelectColumns + "\n" + """
            WHERE dl.id IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1 FROM download_quarantine q
                  WHERE q.source_table = 'downloading' AND q.record_id = db.id)
            ORDER BY db.main_title COLLATE NOCASE, db.[order] ASC, db.id ASC
            """;
        return await DownloadTaskSqlReader
            .ReadManyAsync(connection, command, "downloading", _clock.UtcNow, cancellationToken)
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
        command.CommandText = DownloadTaskSqlReader.SelectColumns + "\n" + """
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
        var items = (await DownloadTaskSqlReader.ReadManyAsync(
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

        _disposed = true;
        _initializationGate.Dispose();
        using var poolKey = new SqliteConnection(_connectionString);
        SqliteConnection.ClearPool(poolKey);
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

}
