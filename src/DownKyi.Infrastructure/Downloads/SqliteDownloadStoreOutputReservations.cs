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
                            task.Metadata.Media.Cid,
                            task.Plan.ActionClaims,
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
            command.CommandText = $"""
                SELECT 1, NULL
                FROM download_base db
                INNER JOIN downloading dl ON dl.id = db.id
                WHERE (db.output_reservation_key = @key
                       OR db.file_path = @file_path COLLATE NOCASE)
                  AND {SqliteDownloadStoreQuarantine.ExcludeDownloadingRecordSql}
                UNION ALL
                SELECT 0, db.file_path
                FROM download_base db
                INNER JOIN downloading dl ON dl.id = db.id
                WHERE db.output_reservation_key IS NULL
                  AND {SqliteDownloadStoreQuarantine.ExcludeDownloadingRecordSql}
                """;
        }
        else
        {
            command.CommandText = $"""
                SELECT 1, NULL
                FROM download_base db
                INNER JOIN downloading dl ON dl.id = db.id
                WHERE (db.output_reservation_key = @key OR db.file_path = @file_path)
                  AND {SqliteDownloadStoreQuarantine.ExcludeDownloadingRecordSql}
                UNION ALL
                SELECT 0, db.file_path
                FROM download_base db
                INNER JOIN downloading dl ON dl.id = db.id
                WHERE db.output_reservation_key IS NULL
                  AND {SqliteDownloadStoreQuarantine.ExcludeDownloadingRecordSql}
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
        command.CommandText = $"""
            SELECT db.file_path
            FROM download_base db
            INNER JOIN downloading dl ON dl.id = db.id
            WHERE {SqliteDownloadStoreQuarantine.ExcludeDownloadingRecordSql}
            """;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            keys.Add(DownloadOutputPathKey.Create(reader.GetString(0), ignoreCase));
        }

        return [.. keys];
    }

    public async Task<bool> IsOutputDirectoryReservedAsync(
        string directory,
        bool ignoreCase,
        CancellationToken cancellationToken)
    {
        var key = DownloadOutputPathKey.Create(directory, ignoreCase);
        if (ignoreCase != DownloadOutputPathKey.UsesCaseInsensitiveComparison)
        {
            var keys = await GetActiveOutputReservationKeysAsync(ignoreCase, cancellationToken)
                .ConfigureAwait(false);
            return keys.Any(candidate => DownloadOutputPathKey.IsSameOrDescendant(candidate, directory, ignoreCase));
        }

        var prefix = Path.EndsInDirectorySeparator(key) ? key : key + Path.DirectorySeparatorChar;
        // The next separator character bounds precisely this directory's descendants.
        var prefixEnd = prefix[..^1] + (char)(prefix[^1] + 1);
        using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT 1, NULL
            FROM download_base db
            INNER JOIN downloading dl ON dl.id = db.id
            WHERE (db.output_reservation_key = @key
                   OR (db.output_reservation_key >= @prefix AND db.output_reservation_key < @prefix_end))
              AND {SqliteDownloadStoreQuarantine.ExcludeDownloadingRecordSql}
            UNION ALL
            SELECT 0, db.file_path
            FROM download_base db
            INNER JOIN downloading dl ON dl.id = db.id
            WHERE db.output_reservation_key IS NULL
              AND {SqliteDownloadStoreQuarantine.ExcludeDownloadingRecordSql}
            """;
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@prefix", prefix);
        command.Parameters.AddWithValue("@prefix_end", prefixEnd);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.GetInt32(0) == 1
                || DownloadOutputPathKey.IsSameOrDescendant(reader.GetString(1), directory, ignoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public async Task<bool> HasOutputClaimConflictAsync(
        string basePath,
        long requestedCid,
        DownloadActionClaims requestedClaims,
        bool ignoreCase,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        using var connection = await _database.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await HasOutputClaimConflictCoreAsync(
                connection,
                transaction: null,
                basePath,
                requestedCid,
                requestedClaims,
                ignoreCase,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<bool> HasOutputClaimConflictCoreAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string basePath,
        long requestedCid,
        DownloadActionClaims requestedClaims,
        bool ignoreCase,
        CancellationToken cancellationToken)
    {
        var key = DownloadOutputPathKey.Create(basePath, ignoreCase);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (ignoreCase)
        {
            command.CommandText = $"""
                SELECT 1, NULL, db.cid, db.need_download_content, db.nfo_request IS NOT NULL
                FROM download_base db
                INNER JOIN downloading dl ON dl.id = db.id
                WHERE (db.output_reservation_key = @key
                       OR db.file_path = @file_path COLLATE NOCASE)
                  AND {SqliteDownloadStoreQuarantine.ExcludeDownloadingRecordSql}
                UNION ALL
                SELECT 0, db.file_path, db.cid, db.need_download_content, db.nfo_request IS NOT NULL
                FROM download_base db
                INNER JOIN downloading dl ON dl.id = db.id
                WHERE db.output_reservation_key IS NULL
                  AND {SqliteDownloadStoreQuarantine.ExcludeDownloadingRecordSql}
                """;
        }
        else
        {
            command.CommandText = $"""
                SELECT 1, NULL, db.cid, db.need_download_content, db.nfo_request IS NOT NULL
                FROM download_base db
                INNER JOIN downloading dl ON dl.id = db.id
                WHERE (db.output_reservation_key = @key OR db.file_path = @file_path)
                  AND {SqliteDownloadStoreQuarantine.ExcludeDownloadingRecordSql}
                UNION ALL
                SELECT 0, db.file_path, db.cid, db.need_download_content, db.nfo_request IS NOT NULL
                FROM download_base db
                INNER JOIN downloading dl ON dl.id = db.id
                WHERE db.output_reservation_key IS NULL
                  AND {SqliteDownloadStoreQuarantine.ExcludeDownloadingRecordSql}
                """;
        }
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@file_path", basePath);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var requestedOwnership = new DownloadOutputOwnership(requestedCid, requestedClaims);
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
                reader.GetString(3),
                "need_download_content");
            var existingClaims = DownloadActionClaims.From(
                existingContent,
                includesNfo: reader.GetInt32(4) != 0);
            var existingOwnership = new DownloadOutputOwnership(reader.GetInt64(2), existingClaims);
            if (existingOwnership.ConflictsWith(requestedOwnership))
            {
                return true;
            }
        }

        return false;
    }
}
