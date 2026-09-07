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

    public Task<OperationResult> UpdateProgressAsync(
        DownloadProgressWrite progressWrite,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progressWrite);
        return _database.ExecuteTransactionAsync(
            async (connection, transaction, token) =>
            {
                using var versionCommand = connection.CreateCommand();
                versionCommand.Transaction = transaction;
                versionCommand.CommandText = """
                    UPDATE download_base
                    SET version = @target_version, updated_at_utc = @updated_at_utc
                    WHERE id = @id AND version = @expected_version
                    """;
                versionCommand.Parameters.AddWithValue("@target_version", progressWrite.TargetVersion);
                versionCommand.Parameters.AddWithValue(
                    "@updated_at_utc",
                    progressWrite.UpdatedAtUtc.ToUnixTimeMilliseconds());
                versionCommand.Parameters.AddWithValue("@id", progressWrite.TaskId.Value);
                versionCommand.Parameters.AddWithValue("@expected_version", progressWrite.ExpectedVersion);
                var changed = await versionCommand.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                if (changed == 0)
                {
                    return DownloadStoreOperationResults.Conflict(
                        progressWrite.TaskId,
                        "has changed since progress was sampled");
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
                changed = await progressCommand.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                return changed == 0
                    ? DownloadStoreOperationResults.NotFound(progressWrite.TaskId)
                    : OperationResult.Success();
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

    public Task<OperationResult> ClearHistoryAsync(CancellationToken cancellationToken)
    {
        return _database.ExecuteTransactionAsync(
            async (connection, transaction, token) =>
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
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                return OperationResult.Success();
            },
            cancellationToken);
    }
}
