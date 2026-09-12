using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Downloads;

internal sealed class SqliteDownloadStoreOutputReservations(SqliteDownloadStoreDatabase database)
{
    private readonly SqliteDownloadStoreDatabase _database = database;

    public Task<OperationResult> AddAsync(
        DownloadTask task,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.Phase == DownloadPhase.Deleted)
        {
            throw new ArgumentException("A deleted task cannot be inserted.", nameof(task));
        }

        return _database.ExecuteImmediateTransactionAsync(
            async (connection, transaction, token) =>
            {
                try
                {
                    if (task.Phase != DownloadPhase.Completed &&
                        await IsOutputPathReservedCoreAsync(
                            connection,
                            transaction,
                            task.Output.BasePath,
                            DownloadOutputPathKey.UsesCaseInsensitiveComparison,
                            token).ConfigureAwait(false))
                    {
                        return DownloadStoreOperationResults.OutputPathConflict();
                    }

                    await DownloadTaskSqlWriter
                        .InsertBaseAsync(connection, transaction, task, token)
                        .ConfigureAwait(false);
                    await DownloadTaskSqlWriter
                        .WriteStateRowAsync(connection, transaction, task, token)
                        .ConfigureAwait(false);
                    return OperationResult.Success();
                }
                catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
                {
                    return exception.Message.Contains(
                        "output_reservation_key",
                        StringComparison.OrdinalIgnoreCase)
                        ? DownloadStoreOperationResults.OutputPathConflict()
                        : DownloadStoreOperationResults.Conflict(task.Id, "already exists");
                }
            },
            cancellationToken);
    }

    public async Task<bool> IsOutputPathReservedAsync(
        string basePath,
        bool ignoreCase,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        if (ignoreCase)
        {
            command.CommandText = """
                SELECT 1, NULL
                FROM download_base db
                INNER JOIN downloading dl ON dl.id = db.id
                WHERE (db.output_reservation_key = @key
                       OR db.file_path = @file_path COLLATE NOCASE)
                  AND NOT EXISTS (
                      SELECT 1 FROM download_quarantine q
                      WHERE q.source_table = 'downloading' AND q.record_id = db.id)
                UNION ALL
                SELECT 0, db.file_path
                FROM download_base db
                INNER JOIN downloading dl ON dl.id = db.id
                WHERE db.output_reservation_key IS NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM download_quarantine q
                      WHERE q.source_table = 'downloading' AND q.record_id = db.id)
                """;
        }
        else
        {
            command.CommandText = """
                SELECT 1, NULL
                FROM download_base db
                INNER JOIN downloading dl ON dl.id = db.id
                WHERE (db.output_reservation_key = @key OR db.file_path = @file_path)
                  AND NOT EXISTS (
                      SELECT 1 FROM download_quarantine q
                      WHERE q.source_table = 'downloading' AND q.record_id = db.id)
                UNION ALL
                SELECT 0, db.file_path
                FROM download_base db
                INNER JOIN downloading dl ON dl.id = db.id
                WHERE db.output_reservation_key IS NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM download_quarantine q
                      WHERE q.source_table = 'downloading' AND q.record_id = db.id)
                """;
        }
        var key = DownloadOutputPathKey.Create(basePath, ignoreCase);
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@file_path", basePath);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.GetInt32(0) == 1 ||
                StringComparer.Ordinal.Equals(
                    DownloadOutputPathKey.Create(reader.GetString(1), ignoreCase), key))
            {
                return true;
            }
        }

        return false;
    }

    public async Task<IReadOnlyList<string>> GetActiveOutputReservationKeysAsync(
        bool ignoreCase,
        CancellationToken cancellationToken)
    {
        using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT db.output_reservation_key, db.file_path
            FROM download_base db
            INNER JOIN downloading dl ON dl.id = db.id
            WHERE NOT EXISTS (
                SELECT 1 FROM download_quarantine q
                WHERE q.source_table = 'downloading' AND q.record_id = db.id)
            """;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false))
            {
                keys.Add(reader.GetString(0));
            }

            // The file path also covers legacy rows with a null reservation key.
            keys.Add(DownloadOutputPathKey.Create(reader.GetString(1), ignoreCase));
        }

        return [.. keys];
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
}
