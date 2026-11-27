using System.Text.Json;
using DownKyi.Application.Downloads;
using DownKyi.Application.Settings;
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
        command.CommandText = DownloadTaskSqlReader.SelectColumns + "\n" + $"""
            WHERE db.id = @id
              AND {SqliteDownloadStoreQuarantine.ExcludeDownloadingRecordSql}
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
            await reader.DisposeAsync().ConfigureAwait(false);
            await SqliteDownloadStoreQuarantine
                .RecordAsync(
                    connection,
                    "downloading",
                    taskId.Value,
                    exception,
                    _clock.UtcNow,
                    cancellationToken)
                .ConfigureAwait(false);
            return null;
        }
    }

    public async Task<IReadOnlyList<DownloadTask>> GetUnfinishedAsync(
        CancellationToken cancellationToken)
    {
        using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = DownloadTaskSqlReader.SelectColumns + "\n" + $"""
            WHERE {SqliteDownloadStoreQuarantine.ExcludeDownloadingRecordSql}
            ORDER BY db.main_title COLLATE NOCASE, db.[order] ASC, db.id ASC
            """;
        return await ReadManyAsync(
            connection,
            command,
            "downloading",
            _clock.UtcNow,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DownloadTask>> GetUnfinishedByCidAsync(
        long cid,
        CancellationToken cancellationToken)
    {
        using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = DownloadTaskSqlReader.SelectColumns + "\n" + $"""
            WHERE db.cid = @cid
              AND {SqliteDownloadStoreQuarantine.ExcludeDownloadingRecordSql}
            ORDER BY db.main_title COLLATE NOCASE, db.[order] ASC, db.id ASC
            """;
        command.Parameters.AddWithValue("@cid", cid);
        return await ReadManyAsync(
            connection,
            command,
            "downloading",
            _clock.UtcNow,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DownloadTask>> GetUnfinishedByCidsAsync(
        IReadOnlyCollection<long> cids,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cids);
        var distinctCids = cids.Distinct().ToArray();
        if (distinctCids.Length == 0)
        {
            return [];
        }

        using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = DownloadTaskSqlReader.SelectColumns + "\n" + $"""
            WHERE db.cid IN (SELECT CAST(value AS INTEGER) FROM json_each(@cids))
              AND {SqliteDownloadStoreQuarantine.ExcludeDownloadingRecordSql}
            ORDER BY db.main_title COLLATE NOCASE, db.[order] ASC, db.id ASC
            """;
        command.Parameters.AddWithValue("@cids", JsonSerializer.Serialize(distinctCids));
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
        CancellationToken cancellationToken,
        DownloadFinishedSort sort = DownloadFinishedSort.DownloadDesc)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, MaximumHistoryPageSize);
        const string select = DownloadHistoryRecordMapper.SelectColumns + "\nWHERE "
            + SqliteDownloadStoreQuarantine.ExcludeDownloadHistoryRecordSql;
        const string ascending = " ORDER BY h.finished_timestamp ASC, h.id ASC LIMIT @limit";
        const string descending = " ORDER BY h.finished_timestamp DESC, h.id DESC LIMIT @limit";
        const string byTitle = " ORDER BY h.main_title COLLATE DOWNKYI_ORDINAL ASC, h.[order] ASC, h.id ASC LIMIT @limit";
        const string afterTime = " AND (h.finished_timestamp, h.id) > (@cursor_timestamp, @cursor_id)";
        const string beforeTime = " AND (h.finished_timestamp, h.id) < (@cursor_timestamp, @cursor_id)";
        const string afterTitle = " AND (h.main_title COLLATE DOWNKYI_ORDINAL, h.[order], h.id) > (@cursor_title, @cursor_order, @cursor_id)";
        using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (sort == DownloadFinishedSort.Number)
        {
            // Keep the UI's UTF-16 ordinal ordering, including supplementary Unicode.
            // Query-only collation leaves old versions and external SQLite readers compatible.
            connection.CreateCollation("DOWNKYI_ORDINAL", string.CompareOrdinal);
        }
        using var command = connection.CreateCommand();
        var cursorTimestamp = command.Parameters.Add("@cursor_timestamp", SqliteType.Integer);
        var cursorId = command.Parameters.Add("@cursor_id", SqliteType.Text);
        var cursorTitle = command.Parameters.Add("@cursor_title", SqliteType.Text);
        var cursorOrder = command.Parameters.Add("@cursor_order", SqliteType.Integer);
        var limit = command.Parameters.Add("@limit", SqliteType.Integer);
        var targetCount = checked(pageSize + 1);
        var items = new List<DownloadHistoryRecord>(targetCount);
        var scanCursor = cursor;
        while (items.Count < targetCount)
        {
            switch (sort, scanCursor == null)
            {
                case (DownloadFinishedSort.DownloadAsc, true): command.CommandText = select + ascending; break;
                case (DownloadFinishedSort.DownloadAsc, false): command.CommandText = select + afterTime + ascending; break;
                case (DownloadFinishedSort.DownloadDesc, true): command.CommandText = select + descending; break;
                case (DownloadFinishedSort.DownloadDesc, false): command.CommandText = select + beforeTime + descending; break;
                case (DownloadFinishedSort.Number, true): command.CommandText = select + byTitle; break;
                case (DownloadFinishedSort.Number, false): command.CommandText = select + afterTitle + byTitle; break;
                default: throw new ArgumentOutOfRangeException(nameof(sort));
            }
            cursorTimestamp.Value = scanCursor?.FinishedTimestamp ?? 0;
            cursorId.Value = scanCursor?.TaskId.Value ?? string.Empty;
            cursorTitle.Value = scanCursor?.MainTitle ?? string.Empty;
            cursorOrder.Value = scanCursor?.Order ?? 0;
            var remainingCount = targetCount - items.Count;
            limit.Value = remainingCount;
            var batch = await ReadHistoryManyAsync(
                    connection,
                    command,
                    _clock.UtcNow,
                    cancellationToken)
                .ConfigureAwait(false);
            items.AddRange(batch.Records);
            if (batch.RowsRead < remainingCount)
            {
                break;
            }

            if (batch.Records.Count > 0)
            {
                var last = batch.Records[^1];
                scanCursor = new DownloadHistoryCursor(last.FinishedTimestamp, last.Id, last.MainTitle, last.Order);
            }
        }

        var hasMore = items.Count > pageSize;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }

        DownloadHistoryCursor? nextCursor = null;
        if (hasMore && items.Count > 0)
        {
            var last = items[^1];
            nextCursor = new DownloadHistoryCursor(last.FinishedTimestamp, last.Id, last.MainTitle, last.Order);
        }

        return new DownloadHistoryPage(items, nextCursor);
    }

    public async Task<IReadOnlyList<DownloadHistoryRecord>> GetHistoryByCidAsync(
        long cid,
        CancellationToken cancellationToken)
    {
        using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = DownloadHistoryRecordMapper.SelectColumns + "\n" + $"""
            WHERE h.cid = @cid
              AND {SqliteDownloadStoreQuarantine.ExcludeDownloadHistoryRecordSql}
            ORDER BY h.finished_timestamp DESC, h.id DESC
            """;
        command.Parameters.AddWithValue("@cid", cid);
        var result = await ReadHistoryManyAsync(
            connection,
            command,
            _clock.UtcNow,
            cancellationToken).ConfigureAwait(false);
        return result.Records;
    }

    public async Task<IReadOnlyList<DownloadHistoryRecord>> GetHistoryByCidsAsync(
        IReadOnlyCollection<long> cids,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cids);
        var distinctCids = cids.Distinct().ToArray();
        if (distinctCids.Length == 0)
        {
            return [];
        }

        using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = DownloadHistoryRecordMapper.SelectColumns + "\n" + $"""
            WHERE h.cid IN (SELECT CAST(value AS INTEGER) FROM json_each(@cids))
              AND {SqliteDownloadStoreQuarantine.ExcludeDownloadHistoryRecordSql}
            ORDER BY h.finished_timestamp DESC, h.id DESC
            """;
        command.Parameters.AddWithValue("@cids", JsonSerializer.Serialize(distinctCids));
        var result = await ReadHistoryManyAsync(
            connection,
            command,
            _clock.UtcNow,
            cancellationToken).ConfigureAwait(false);
        return result.Records;
    }

    private static async Task<(IReadOnlyList<DownloadHistoryRecord> Records, int RowsRead)>
        ReadHistoryManyAsync(
        SqliteConnection connection,
        SqliteCommand command,
        DateTimeOffset quarantinedAtUtc,
        CancellationToken cancellationToken)
    {
        var records = new List<DownloadHistoryRecord>();
        var corrupt = new List<(string RecordId, DownloadRecordCorruptException Error)>();
        var rowsRead = 0;
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rowsRead++;
                var recordId = reader.GetString(reader.GetOrdinal("id"));
                try
                {
                    records.Add(DownloadHistoryRecordMapper.Read(reader));
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
                    "download_history",
                    item.RecordId,
                    item.Error,
                    quarantinedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return (records, rowsRead);
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
