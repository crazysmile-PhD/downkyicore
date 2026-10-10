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
        if (task.Phase is DownloadPhase.Completed or DownloadPhase.Deleted)
        {
            throw new ArgumentException(
                "Only recoverable download tasks can be inserted as active tasks.",
                nameof(task));
        }

        return _database.ExecuteImmediateTransactionAsync(
            async (connection, transaction, token) =>
            {
                try
                {
                    using (var history = connection.CreateCommand())
                    {
                        history.Transaction = transaction;
                        history.CommandText =
                            "SELECT EXISTS (SELECT 1 FROM download_history WHERE id = @id)";
                        history.Parameters.AddWithValue("@id", task.Id.Value);
                        if (Convert.ToInt64(
                                await history.ExecuteScalarAsync(token).ConfigureAwait(false),
                                System.Globalization.CultureInfo.InvariantCulture) != 0)
                        {
                            return DownloadStoreOperationResults.Conflict(
                                task.Id,
                                "already exists as history");
                        }
                    }

                    if (await HasOutputClaimConflictCoreAsync(
                            connection,
                            transaction,
                            task.Output.BasePath,
                            task.Plan.RequestedContent,
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
                    return DownloadStoreOperationResults.Conflict(task.Id, "already exists");
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
        if (ignoreCase != DownloadOutputPathKey.UsesCaseInsensitiveComparison)
        {
            // Persisted keys only index the policy of this store's platform.
            // An explicit alternate-policy query must derive identities from paths.
            var requestedKey = DownloadOutputPathKey.Create(basePath, ignoreCase);
            var keys = await GetActiveOutputReservationKeysAsync(ignoreCase, cancellationToken)
                .ConfigureAwait(false);
            return keys.Contains(requestedKey, StringComparer.Ordinal);
        }

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
            SELECT db.file_path
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
            keys.Add(DownloadOutputPathKey.Create(reader.GetString(0), ignoreCase));
        }

        return [.. keys];
    }

    public async Task<bool> HasOutputClaimConflictAsync(
        string basePath,
        DownloadContentSelection requestedContent,
        bool ignoreCase,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        ArgumentNullException.ThrowIfNull(requestedContent);
        using var connection = await _database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await HasOutputClaimConflictCoreAsync(
                connection,
                transaction: null,
                basePath,
                requestedContent,
                ignoreCase,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<bool> HasOutputClaimConflictCoreAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string basePath,
        DownloadContentSelection requestedContent,
        bool ignoreCase,
        CancellationToken cancellationToken)
    {
        var key = DownloadOutputPathKey.Create(basePath, ignoreCase);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (ignoreCase)
        {
            command.CommandText = """
                SELECT 1, NULL, db.need_download_content
                FROM download_base db
                INNER JOIN downloading dl ON dl.id = db.id
                WHERE (db.output_reservation_key = @key
                       OR db.file_path = @file_path COLLATE NOCASE)
                  AND NOT EXISTS (
                      SELECT 1 FROM download_quarantine q
                      WHERE q.source_table = 'downloading' AND q.record_id = db.id)
                UNION ALL
                SELECT 0, db.file_path, db.need_download_content
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
                SELECT 1, NULL, db.need_download_content
                FROM download_base db
                INNER JOIN downloading dl ON dl.id = db.id
                WHERE (db.output_reservation_key = @key OR db.file_path = @file_path)
                  AND NOT EXISTS (
                      SELECT 1 FROM download_quarantine q
                      WHERE q.source_table = 'downloading' AND q.record_id = db.id)
                UNION ALL
                SELECT 0, db.file_path, db.need_download_content
                FROM download_base db
                INNER JOIN downloading dl ON dl.id = db.id
                WHERE db.output_reservation_key IS NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM download_quarantine q
                      WHERE q.source_table = 'downloading' AND q.record_id = db.id)
                """;
        }
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@file_path", basePath);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.GetInt32(0) != 1
                && !StringComparer.Ordinal.Equals(
                    DownloadOutputPathKey.Create(reader.GetString(1), ignoreCase),
                    key))
            {
                continue;
            }

            var existingContent = DownloadStoreJson.ReadContentSelection(
                reader.GetString(2),
                "need_download_content");
            if (existingContent.ActionClaims.Overlaps(requestedContent.ActionClaims))
            {
                return true;
            }
        }

        return false;
    }
}
