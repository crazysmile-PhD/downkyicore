using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;
using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Downloads;

internal sealed class SqliteDownloadStoreQuarantine(SqliteDownloadStoreDatabase database)
{
    private readonly SqliteDownloadStoreDatabase _database = database;

    public async Task<IReadOnlyList<QuarantinedDownloadRecord>> GetRecordsAsync(
        CancellationToken cancellationToken)
    {
        using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT quarantine_id, source_table, record_id, field_name, reason, quarantined_at_utc
            FROM download_quarantine
            ORDER BY quarantine_id ASC
            """;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var records = new List<QuarantinedDownloadRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(new QuarantinedDownloadRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5))));
        }

        return records;
    }

    public static async Task RecordAsync(
        SqliteConnection connection,
        string sourceTable,
        string recordId,
        DownloadRecordCorruptException error,
        DateTimeOffset quarantinedAtUtc,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO download_quarantine
                (source_table, record_id, field_name, reason, quarantined_at_utc)
            VALUES (@source_table, @record_id, @field_name, @reason, @quarantined_at_utc)
            ON CONFLICT(source_table, record_id) DO UPDATE SET
                field_name = excluded.field_name,
                reason = excluded.reason,
                quarantined_at_utc = excluded.quarantined_at_utc
            """;
        command.Parameters.AddWithValue("@source_table", sourceTable);
        command.Parameters.AddWithValue("@record_id", recordId);
        command.Parameters.AddWithValue("@field_name", error.FieldName);
        command.Parameters.AddWithValue("@reason", error.Message);
        command.Parameters.AddWithValue("@quarantined_at_utc", quarantinedAtUtc.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
