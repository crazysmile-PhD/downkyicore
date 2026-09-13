using DownKyi.Application.Downloads;
using DownKyi.Application.Time;
using DownKyi.Domain.Downloads;
using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Downloads;

internal sealed class SqliteDownloadStoreQueries(
    SqliteDownloadStoreDatabase database,
    IClock clock)
{
    private const int MaximumHistoryPageSize = 500;
    private readonly SqliteDownloadStoreDatabase _database = database;
    private readonly IClock _clock = clock;

    public async Task<DownloadTask?> FindAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
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
            await SqliteDownloadStoreQuarantine
                .RecordAsync(connection, source, taskId.Value, exception, _clock.UtcNow, cancellationToken)
                .ConfigureAwait(false);
            return null;
        }
    }

    public async Task<IReadOnlyList<DownloadTask>> GetUnfinishedAsync(
        CancellationToken cancellationToken)
    {
        using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = DownloadTaskSqlReader.SelectColumns + "\n" + """
            WHERE dl.id IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1 FROM download_quarantine q
                  WHERE q.source_table = 'downloading' AND q.record_id = db.id)
            ORDER BY db.main_title COLLATE NOCASE, db.[order] ASC, db.id ASC
            """;
        return await ReadManyAsync(
            connection,
            command,
            "downloading",
            _clock.UtcNow,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DownloadHistoryPage> GetHistoryPageAsync(
        DownloadHistoryCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, MaximumHistoryPageSize);
        using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
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
                    tasks.Add(DownloadTaskRecordMapper.Read(reader));
                }
                catch (DownloadRecordCorruptException exception)
                {
                    corrupt.Add((recordId, exception));
                }
            }
        }

        foreach (var item in corrupt)
        {
            await SqliteDownloadStoreQuarantine
                .RecordAsync(
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
}
