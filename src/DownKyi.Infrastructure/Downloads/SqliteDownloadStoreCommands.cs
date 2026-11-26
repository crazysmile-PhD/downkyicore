using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;

namespace DownKyi.Infrastructure.Downloads;

internal sealed class SqliteDownloadStoreCommands(SqliteDownloadStoreDatabase database)
{
    private readonly SqliteDownloadStoreDatabase _database = database;

    public Task<OperationResult> UpdateAsync(
        DownloadTask task,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.Phase == DownloadPhase.Completed)
        {
            throw new ArgumentException(
                "Completed tasks must be committed through the history transition.",
                nameof(task));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(expectedVersion, task.Version);

        return _database.ExecuteTransactionAsync(
            async (connection, transaction, token) =>
            {
                var updated = await DownloadTaskSqlWriter.UpdateBaseAsync(
                    connection,
                    transaction,
                    task,
                    expectedVersion,
                    token).ConfigureAwait(false);
                if (updated == 0)
                {
                    return DownloadStoreOperationResults.Conflict(
                        task.Id,
                        "has changed since it was loaded");
                }

                if (task.Phase == DownloadPhase.Deleted)
                {
                    await DownloadTaskSqlWriter
                        .DeleteBaseAsync(connection, transaction, task.Id, token)
                        .ConfigureAwait(false);
                }
                else
                {
                    await DownloadTaskSqlWriter
                        .WriteStateRowAsync(connection, transaction, task, token)
                        .ConfigureAwait(false);
                }

                return OperationResult.Success();
            },
            cancellationToken);
    }

    public Task<OperationResult> CompleteAsync(
        DownloadTask task,
        DownloadHistoryRecord history,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);
        if (task.Phase != DownloadPhase.Completed || task.Id != history.Id)
        {
            throw new ArgumentException(
                "The completed task and history record must describe the same task.",
                nameof(task));
        }

        return _database.ExecuteTransactionAsync(
            async (connection, transaction, token) =>
            {
                using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM download_base WHERE id = @id AND version = @expected_version";
                delete.Parameters.AddWithValue("@id", task.Id.Value);
                delete.Parameters.AddWithValue("@expected_version", expectedVersion);
                var deleted = await delete.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                if (deleted == 0)
                {
                    return DownloadStoreOperationResults.Conflict(
                        task.Id,
                        "has changed since it was loaded");
                }

                try
                {
                    await DownloadHistorySqlWriter
                        .InsertAsync(connection, transaction, history, token)
                        .ConfigureAwait(false);
                }
                catch (Microsoft.Data.Sqlite.SqliteException exception)
                    when (exception.SqliteErrorCode == 19)
                {
                    return DownloadStoreOperationResults.Conflict(task.Id, "already has history");
                }

                return OperationResult.Success();
            },
            cancellationToken);
    }

    public Task<OperationResult> AddHistoryAsync(
        DownloadHistoryRecord history,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(history);
        return _database.ExecuteTransactionAsync(
            async (connection, transaction, token) =>
            {
                using (var active = connection.CreateCommand())
                {
                    active.Transaction = transaction;
                    active.CommandText = "SELECT EXISTS (SELECT 1 FROM download_base WHERE id = @id)";
                    active.Parameters.AddWithValue("@id", history.Id.Value);
                    if (Convert.ToInt64(
                            await active.ExecuteScalarAsync(token).ConfigureAwait(false),
                            System.Globalization.CultureInfo.InvariantCulture) != 0)
                    {
                        return DownloadStoreOperationResults.Conflict(
                            history.Id,
                            "already exists as an active task");
                    }
                }

                try
                {
                    await DownloadHistorySqlWriter
                        .InsertAsync(connection, transaction, history, token)
                        .ConfigureAwait(false);
                    return OperationResult.Success();
                }
                catch (Microsoft.Data.Sqlite.SqliteException exception)
                    when (exception.SqliteErrorCode == 19)
                {
                    return OperationResult.Success();
                }
            },
            cancellationToken);
    }

    public async Task<OperationResult> DeleteAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM download_base WHERE id = @id";
        command.Parameters.AddWithValue("@id", taskId.Value);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return changed == 0
            ? DownloadStoreOperationResults.NotFound(taskId)
            : OperationResult.Success();
    }

    public async Task<OperationResult> DeleteHistoryAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM download_history WHERE id = @id";
        command.Parameters.AddWithValue("@id", taskId.Value);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return changed == 0
            ? DownloadStoreOperationResults.NotFound(taskId)
            : OperationResult.Success();
    }

    public Task<OperationResult> ClearHistoryAsync(CancellationToken cancellationToken)
    {
        return _database.ExecuteTransactionAsync(
            async (connection, transaction, token) =>
            {
                const string sql = "DELETE FROM download_history;";
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                return OperationResult.Success();
            },
            cancellationToken);
    }
}
