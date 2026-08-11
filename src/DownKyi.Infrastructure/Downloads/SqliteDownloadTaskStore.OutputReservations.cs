using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Downloads;

public sealed partial class SqliteDownloadTaskStore
{
    public async Task<OperationResult> AddAsync(
        DownloadTask task,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.Phase == DownloadPhase.Deleted)
        {
            throw new ArgumentException("A deleted task cannot be inserted.", nameof(task));
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediateTransaction(connection);
        try
        {
            if (task.Phase != DownloadPhase.Completed &&
                await IsOutputPathReservedCoreAsync(
                    connection,
                    transaction,
                    task.Output.BasePath,
                    DownloadOutputPathKey.UsesCaseInsensitiveComparison,
                    cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return OutputPathConflict();
            }

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
            return exception.Message.Contains(
                "output_reservation_key",
                StringComparison.OrdinalIgnoreCase)
                ? OutputPathConflict()
                : Conflict(task.Id, "already exists");
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<bool> IsOutputPathReservedAsync(
        string basePath,
        bool ignoreCase,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await IsOutputPathReservedCoreAsync(
            connection,
            transaction: null,
            basePath,
            ignoreCase,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> IsOutputPathReservedCoreAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string basePath,
        bool ignoreCase,
        CancellationToken cancellationToken)
    {
        var key = DownloadOutputPathKey.Create(basePath, ignoreCase);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (ignoreCase)
        {
            command.CommandText = """
                SELECT EXISTS (
                    SELECT 1
                    FROM download_base db
                    INNER JOIN downloading dl ON dl.id = db.id
                    WHERE (db.output_reservation_key = @key
                           OR db.file_path = @file_path COLLATE NOCASE)
                      AND NOT EXISTS (
                          SELECT 1 FROM download_quarantine q
                          WHERE q.source_table = 'downloading' AND q.record_id = db.id))
                """;
        }
        else
        {
            command.CommandText = """
                SELECT EXISTS (
                    SELECT 1
                    FROM download_base db
                    INNER JOIN downloading dl ON dl.id = db.id
                    WHERE (db.output_reservation_key = @key OR db.file_path = @file_path)
                      AND NOT EXISTS (
                          SELECT 1 FROM download_quarantine q
                          WHERE q.source_table = 'downloading' AND q.record_id = db.id))
                """;
        }

        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@file_path", basePath);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private static OperationResult OutputPathConflict()
    {
        return OperationResult.Failure(new OperationError(
            "download.store.output_path_reserved",
            "The selected output path is already reserved by another active download.",
            OperationErrorKind.Conflict));
    }

    private static SqliteTransaction BeginImmediateTransaction(SqliteConnection connection) =>
        connection.BeginTransaction(deferred: false);

}
