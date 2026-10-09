using System.Collections.Immutable;
using DownKyi.Application.Downloads;
using DownKyi.Application.Time;
using DownKyi.Domain.Downloads;
using DownKyi.Infrastructure.Downloads;
using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Tests;

internal sealed partial class SqliteDownloadStoreFixture
{
    internal Task<SqliteConnection> OpenReadOnlyConnectionAsync() =>
        OpenConnectionAsync(readOnly: true);

    internal async Task<long> CountDownloadBaseRecordAsync(string id)
    {
        using var connection = await OpenReadOnlyConnectionAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM download_base WHERE id = @id";
        command.Parameters.AddWithValue("@id", id);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken).ConfigureAwait(false))!;
    }

    internal async Task<long> CountDownloadingRecordAsync(string id)
    {
        using var connection = await OpenReadOnlyConnectionAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM downloading WHERE id = @id";
        command.Parameters.AddWithValue("@id", id);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken).ConfigureAwait(false))!;
    }

    internal async Task<long> CountHistoryRecordAsync(string id)
    {
        using var connection = await OpenReadOnlyConnectionAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM download_history WHERE id = @id";
        command.Parameters.AddWithValue("@id", id);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken).ConfigureAwait(false))!;
    }

    internal async Task InsertHistoryBypassingOwnershipCheckAsync(DownloadHistoryRecord history)
    {
        using var connection = await OpenConnectionAsync(readOnly: false).ConfigureAwait(false);
        using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(false);
        await DownloadHistorySqlWriter.InsertAsync(
            connection,
            transaction,
            history,
            TestContext.Current.CancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    internal async Task<long> ReadSchemaVersionAsync()
    {
        using var connection = await OpenReadOnlyConnectionAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken).ConfigureAwait(false))!;
    }

    internal async Task InsertPreexistingQuarantineAsync(string id)
    {
        using var connection = await OpenConnectionAsync(readOnly: false).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO download_quarantine
                (source_table, record_id, field_name, reason, quarantined_at_utc)
            VALUES ('downloading', @id, 'need_download_content',
                    'preexisting-corrupt-record', @now)
            """;
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@now", _clock.UtcNow.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    internal async Task SetReservationKeyAsync(string id, string? key)
    {
        using var connection = await OpenConnectionAsync(readOnly: false).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE download_base SET output_reservation_key = @key WHERE id = @id
            """;
        command.Parameters.AddWithValue("@key", key ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@id", id);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(false));
    }

    internal async Task SetPathAndReservationKeyAsync(string id, string path, string? key)
    {
        using var connection = await OpenConnectionAsync(readOnly: false).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE download_base SET file_path = @path, output_reservation_key = @key
            WHERE id = @id
            """;
        command.Parameters.AddWithValue("@path", path);
        command.Parameters.AddWithValue("@key", key ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@id", id);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(false));
    }

    internal async Task<string?> ReadReservationKeyAsync(string id)
    {
        using var connection = await OpenReadOnlyConnectionAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT output_reservation_key FROM download_base WHERE id = @id";
        command.Parameters.AddWithValue("@id", id);
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(false) as string;
    }

    internal async Task<(string StagingToken, string PublishingKey, string PublishedArtifacts,
        double Progress)> ReadPublicationPayloadAsync(string id)
    {
        using var connection = await OpenReadOnlyConnectionAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT db.staging_token, db.publishing_key, db.published_artifacts, dl.progress
            FROM download_base db INNER JOIN downloading dl ON dl.id = db.id
            WHERE db.id = @id
            """;
        command.Parameters.AddWithValue("@id", id);
        using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(false));
        return (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetDouble(3));
    }

    internal async Task CreateQuarantineFailureTriggerAsync()
    {
        using var connection = await OpenConnectionAsync(readOnly: false).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TRIGGER reject_version_four_quarantine
            BEFORE INSERT ON download_quarantine
            BEGIN
                SELECT RAISE(ABORT, 'synthetic version four failure');
            END;
            """;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    internal async Task<StoredDownloadState> ReadStoredStateAsync(string id)
    {
        using var connection = await OpenReadOnlyConnectionAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT db.file_path, dl.gid, dl.download_files, dl.downloaded_files,
                   NULL AS finished_timestamp
            FROM download_base db
            LEFT JOIN downloading dl ON dl.id = db.id
            WHERE db.id = @id
            """;
        command.Parameters.AddWithValue("@id", id);
        using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(false));
        var gidIsNull = await reader.IsDBNullAsync(1, TestContext.Current.CancellationToken)
            .ConfigureAwait(false);
        var downloadFilesIsNull = await reader.IsDBNullAsync(2, TestContext.Current.CancellationToken)
            .ConfigureAwait(false);
        var downloadedFilesIsNull = await reader.IsDBNullAsync(3, TestContext.Current.CancellationToken)
            .ConfigureAwait(false);
        var finishedTimestampIsNull = await reader.IsDBNullAsync(4, TestContext.Current.CancellationToken)
            .ConfigureAwait(false);
        return new StoredDownloadState(
            reader.GetString(0),
            gidIsNull ? null : reader.GetString(1),
            downloadFilesIsNull ? null : reader.GetString(2),
            downloadedFilesIsNull ? null : reader.GetString(3),
            finishedTimestampIsNull ? null : reader.GetInt64(4));
    }

    internal async Task<bool> TableExistsAsync(string tableName)
    {
        using var connection = await OpenReadOnlyConnectionAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM sqlite_master
                WHERE type = 'table' AND name = @name)
            """;
        command.Parameters.AddWithValue("@name", tableName);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(false))! != 0;
    }

    internal async Task<IReadOnlyList<string>> ReadTableColumnsAsync(string tableName)
    {
        using var connection = await OpenReadOnlyConnectionAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info(@table_name) ORDER BY cid";
        command.Parameters.AddWithValue("@table_name", tableName);
        using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(false);
        var columns = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(false))
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    internal async Task<long> CountSchemaMigrationAsync(int version)
    {
        using var connection = await OpenReadOnlyConnectionAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM download_schema_migrations WHERE version = @version";
        command.Parameters.AddWithValue("@version", version);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(false))!;
    }

    internal async Task<long> CountQuarantineRecordsAsync()
    {
        using var connection = await OpenReadOnlyConnectionAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM download_quarantine";
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(false))!;
    }

    internal async Task CorruptRequestedAssetsAsync(string id, string sensitiveValue)
    {
        using var connection = await OpenConnectionAsync(readOnly: false).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE download_base SET need_download_content = @value WHERE id = @id";
        command.Parameters.AddWithValue("@value", sensitiveValue);
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    internal async Task CreateHistoryInsertFailureTriggerAsync()
    {
        using var connection = await OpenConnectionAsync(readOnly: false).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TRIGGER reject_history_insert
            BEFORE INSERT ON download_history
            BEGIN
                INSERT INTO missing_history_sink(id) VALUES (NEW.id);
            END;
            """;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    internal async Task CorruptHistoryPublishedArtifactsAsync(string id)
    {
        using var connection = await OpenConnectionAsync(readOnly: false).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE download_history SET published_artifacts = '{' WHERE id = @id";
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    internal async Task ReplaceNfoRequestAsync(string id, string payload)
    {
        using var connection = await OpenConnectionAsync(readOnly: false).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE download_base SET nfo_request = @payload WHERE id = @id";
        command.Parameters.AddWithValue("@payload", payload);
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    internal Task<SqliteConnection> OpenConnectionAsync(bool readOnly) =>
        OpenConnectionAsync(readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite);

    internal Task<SqliteConnection> OpenNewDatabaseAsync()
    {
        Directory.CreateDirectory(_directory);
        return OpenConnectionAsync(SqliteOpenMode.ReadWriteCreate);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "download.db"),
            Mode = mode,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        return connection;
    }
}
