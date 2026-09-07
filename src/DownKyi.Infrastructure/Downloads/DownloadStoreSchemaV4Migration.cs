using DownKyi.Application.Downloads;
using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Downloads;

internal static class DownloadStoreSchemaV4Migration
{
    private const string QuarantineReason = "legacy-output-path-unverified";

    public static async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset appliedAtUtc,
        IPhysicalOutputPathResolver physicalOutputPathResolver,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(physicalOutputPathResolver);

        await CreateAdmissionGateAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        var legacyTasks = await ReadLegacyUnfinishedTasksAsync(
            connection,
            transaction,
            cancellationToken).ConfigureAwait(false);
        foreach (var task in legacyTasks)
        {
            if (IsSamePhysicalPath(task.BasePath, physicalOutputPathResolver))
            {
                continue;
            }

            await QuarantineAndBlockAdmissionAsync(
                connection,
                transaction,
                task.Id,
                appliedAtUtc,
                cancellationToken).ConfigureAwait(false);
        }

        await DownloadStoreSchemaLifecycle
            .RecordMigrationAsync(connection, transaction, 4, appliedAtUtc, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task CreateAdmissionGateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS download_upgrade_admission_gate (
                singleton_id INTEGER PRIMARY KEY CHECK(singleton_id = 1),
                remote_stopped_confirmed INTEGER NOT NULL DEFAULT 0
                    CHECK(remote_stopped_confirmed IN (0, 1))
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<LegacyUnfinishedTask>> ReadLegacyUnfinishedTasksAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT db.id, db.file_path
            FROM download_base db
            INNER JOIN downloading dl ON dl.id = db.id
            WHERE NOT EXISTS (
                      SELECT 1 FROM downloaded d
                      WHERE d.id = db.id)
            ORDER BY db.id
            """;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var tasks = new List<LegacyUnfinishedTask>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tasks.Add(new LegacyUnfinishedTask(reader.GetString(0), reader.GetString(1)));
        }

        return tasks;
    }

    private static bool IsSamePhysicalPath(
        string originalPath,
        IPhysicalOutputPathResolver physicalOutputPathResolver)
    {
        try
        {
            var ignoreCase = DownloadOutputPathKey.UsesCaseInsensitiveComparison;
            var originalKey = DownloadOutputPathKey.Create(originalPath, ignoreCase);
            var physicalKey = DownloadOutputPathKey.Create(
                physicalOutputPathResolver.ResolvePhysicalBasePath(originalPath),
                ignoreCase);
            return StringComparer.Ordinal.Equals(originalKey, physicalKey);
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static async Task QuarantineAndBlockAdmissionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string taskId,
        DateTimeOffset quarantinedAtUtc,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO download_quarantine
                (source_table, record_id, field_name, reason, quarantined_at_utc)
            VALUES ('downloading', @record_id, 'file_path', @reason, @quarantined_at_utc)
            ON CONFLICT(source_table, record_id) DO NOTHING;

            INSERT INTO download_upgrade_admission_gate
                (singleton_id, remote_stopped_confirmed)
            VALUES (1, 0)
            ON CONFLICT(singleton_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("@record_id", taskId);
        command.Parameters.AddWithValue("@reason", QuarantineReason);
        command.Parameters.AddWithValue(
            "@quarantined_at_utc",
            quarantinedAtUtc.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record LegacyUnfinishedTask(string Id, string BasePath);
}
