using DownKyi.Application.Time;
using DownKyi.Domain.Results;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DownKyi.Infrastructure.Downloads;

internal sealed class SqliteDownloadStoreDatabase : IDisposable
{
    private static readonly Action<ILogger, int, Exception?> LogOrphanCleanup = LoggerMessage.Define<int>(
        LogLevel.Information,
        new EventId(1001, "DownloadStoreOrphanCleanup"),
        "Removed {Count} orphaned downloading records.");
    private readonly SqliteDownloadTaskStoreOptions _options;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private volatile bool _initialized;
    private bool _disposed;

    public SqliteDownloadStoreDatabase(
        SqliteDownloadTaskStoreOptions options,
        IClock clock,
        ILogger logger)
    {
        _options = options;
        _clock = clock;
        _logger = logger;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = checked((int)Math.Ceiling(options.BusyTimeout.TotalSeconds))
        }.ToString();
    }

    public Task InitializeAsync(CancellationToken cancellationToken) =>
        EnsureInitializedAsync(cancellationToken);

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await OpenConnectionCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<OperationResult> ExecuteTransactionAsync(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<OperationResult>> operation,
        CancellationToken cancellationToken) =>
        ExecuteTransactionCoreAsync(operation, immediate: false, cancellationToken);

    public Task<OperationResult> ExecuteImmediateTransactionAsync(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<OperationResult>> operation,
        CancellationToken cancellationToken) =>
        ExecuteTransactionCoreAsync(operation, immediate: true, cancellationToken);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _initializationGate.Dispose();
    }

    private async Task<OperationResult> ExecuteTransactionCoreAsync(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<OperationResult>> operation,
        bool immediate,
        CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = immediate
            ? BeginImmediateTransaction(connection)
            : (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
        try
        {
            var result = await operation(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }

            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized)
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            var databaseExisted = File.Exists(_options.DatabasePath)
                && new FileInfo(_options.DatabasePath).Length > 0;
            Directory.CreateDirectory(Path.GetDirectoryName(_options.DatabasePath) ?? ".");
            using var connection = await OpenConnectionCoreAsync(cancellationToken).ConfigureAwait(false);
            await DownloadStoreSchema.InitializeAsync(
                connection,
                _options.DatabasePath,
                databaseExisted,
                _clock,
                cancellationToken).ConfigureAwait(false);
            await RemoveOrphanedDownloadingRecordsAsync(connection, cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private async Task RemoveOrphanedDownloadingRecordsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM downloading
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM download_base
                    WHERE download_base.id = downloading.id)
                """;
            var removed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            if (removed > 0)
            {
                LogOrphanCleanup(_logger, removed, null);
            }
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<SqliteConnection> OpenConnectionCoreAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA foreign_keys = ON;
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA temp_store = MEMORY;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static SqliteTransaction BeginImmediateTransaction(SqliteConnection connection) =>
        connection.BeginTransaction(deferred: false);
}
