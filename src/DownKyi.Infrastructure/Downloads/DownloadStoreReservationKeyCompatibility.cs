using DownKyi.Application.Downloads;
using DownKyi.Application.Time;
using DownKyi.Domain.Downloads;
using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Downloads;

// A store-open invariant, not a schema-version migration: the same schema can
// contain keys written under a different platform's comparison policy.
internal static class DownloadStoreReservationKeyCompatibility
{
    private const string BlockedMessage =
        "Download reservation keys cannot be reconciled; downloads are blocked to protect existing tasks.";

    public static async Task EnsureAsync(
        SqliteConnection connection,
        string databasePath,
        IClock clock,
        CancellationToken cancellationToken)
    {
        // IMMEDIATE excludes other writers through read, backup and commit. A
        // separate backup connection sees the committed pre-update WAL snapshot.
        using var transaction = SqliteDownloadStoreDatabase.BeginImmediateTransaction(connection);
        try
        {
            var rows = await ReadRowsAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            var targetClaims = new Dictionary<string, List<DownloadActionClaims>>(
                StringComparer.Ordinal);
            var changes = new List<(string Id, string Target)>();
            foreach (var row in rows)
            {
                var target = CreateCurrentKey(row.Path);
                if (!targetClaims.TryGetValue(target, out var existingClaims))
                {
                    existingClaims = [];
                    targetClaims.Add(target, existingClaims);
                }

                if (existingClaims.Any(claims => claims.Overlaps(row.Claims)))
                {
                    throw new InvalidOperationException(BlockedMessage);
                }

                existingClaims.Add(row.Claims);

                if (!StringComparer.Ordinal.Equals(row.StoredKey, target))
                {
                    changes.Add((row.Id, target));
                }
            }

            if (changes.Count > 0)
            {
                var backupConnectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false
                }.ToString();
                using (var backupSource = new SqliteConnection(backupConnectionString))
                {
                    await backupSource.OpenAsync(cancellationToken).ConfigureAwait(false);
                    await DownloadStoreSchemaLifecycle.BackupReservationKeysAsync(
                        backupSource, databasePath, clock, cancellationToken).ConfigureAwait(false);
                }

                foreach (var (id, target) in changes)
                {
                    await SetKeyAsync(connection, transaction, id, target, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<IReadOnlyList<ReservationRow>> ReadRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT db.id, db.file_path, db.output_reservation_key,
                   db.need_download_content, db.nfo_request IS NOT NULL
            FROM download_base db
            INNER JOIN downloading dl ON dl.id = db.id
            LEFT JOIN download_quarantine q
                   ON q.source_table = 'downloading' AND q.record_id = db.id
            WHERE q.record_id IS NULL
            ORDER BY db.id
            """;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<ReservationRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new ReservationRow(
                reader.GetString(0),
                reader.GetString(1),
                await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false)
                    ? null : reader.GetString(2),
                DownloadActionClaims.From(
                    DownloadStoreJson.ReadContentSelection(
                        reader.GetString(3),
                        "need_download_content"),
                    includesNfo: reader.GetInt32(4) != 0)));
        }

        return rows;
    }

    internal static string CreateCurrentKey(string path)
    {
        // GetFullPath would reinterpret a foreign absolute path as a local
        // relative/root-relative path. Such data needs explicit user repair.
        if ((OperatingSystem.IsWindows()
                && path.StartsWith('/') && !Path.IsPathFullyQualified(path))
            || (!OperatingSystem.IsWindows()
                && (path.StartsWith("//", StringComparison.Ordinal)
                    || path.StartsWith('\\')
                    || (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':'))))
        {
            throw new InvalidOperationException(BlockedMessage);
        }

        try
        {
            return DownloadOutputPathKey.Create(
                path, DownloadOutputPathKey.UsesCaseInsensitiveComparison);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            throw new InvalidOperationException(BlockedMessage);
        }
    }

    private static async Task SetKeyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string id,
        string? key,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE download_base SET output_reservation_key = @key WHERE id = @id
            """;
        command.Parameters.AddWithValue("@key", key ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@id", id);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException(BlockedMessage);
        }
    }

    private sealed record ReservationRow(
        string Id,
        string Path,
        string? StoredKey,
        DownloadActionClaims Claims);
}
