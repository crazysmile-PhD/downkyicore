using DownKyi.Application.Downloads;
using DownKyi.Application.Settings;
using DownKyi.Application.Time;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Infrastructure.Downloads;

public sealed class SqliteDownloadTaskStore :
    IDownloadTaskStore,
    IDownloadHistoryStore,
    IDownloadCompletionStore,
    IDownloadStoreRecovery,
    IDisposable
{
    private readonly SqliteDownloadStoreDatabase _database;
    private readonly SqliteDownloadStoreQueries _queries;
    private readonly SqliteDownloadStoreCommands _commands;
    private readonly SqliteDownloadStoreOutputReservations _outputReservations;
    private readonly SqliteDownloadStoreQuarantine _quarantine;

    public SqliteDownloadTaskStore(SqliteDownloadTaskStoreOptions options, IClock clock)
        : this(options, clock, NullLogger<SqliteDownloadTaskStore>.Instance)
    {
    }

    public SqliteDownloadTaskStore(
        SqliteDownloadTaskStoreOptions options,
        IClock clock,
        ILogger<SqliteDownloadTaskStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        if (options.BusyTimeout <= TimeSpan.Zero || options.BusyTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        _database = new SqliteDownloadStoreDatabase(
            options,
            clock,
            logger,
            typeof(SqliteDownloadTaskStore));
        _quarantine = new SqliteDownloadStoreQuarantine(_database);
        _queries = new SqliteDownloadStoreQueries(_database, clock);
        _commands = new SqliteDownloadStoreCommands(_database);
        _outputReservations = new SqliteDownloadStoreOutputReservations(_database);
    }

    // SQLite is synchronous. Dispatch foreground calls, and reuse an existing
    // pool thread without a synchronization context for background calls.
    public Task InitializeAsync(CancellationToken cancellationToken) =>
        ExecuteAsync(() => _database.InitializeAsync(cancellationToken), cancellationToken);

    public Task BackupAndResetAsync(CancellationToken cancellationToken) =>
        ExecuteAsync(() => _database.BackupAndResetAsync(cancellationToken), cancellationToken);

    public Task<OperationResult> AddAsync(DownloadTask task, CancellationToken cancellationToken) =>
        ExecuteAsync(() => _outputReservations.AddAsync(task, cancellationToken), cancellationToken);

    public Task<OperationResult> AddHistoryAsync(
        DownloadHistoryRecord history,
        CancellationToken cancellationToken) =>
        ExecuteAsync(() => _commands.AddHistoryAsync(history, cancellationToken), cancellationToken);

    public Task<OperationResult> UpdateAsync(
        DownloadTask task,
        long expectedVersion,
        CancellationToken cancellationToken) =>
        ExecuteAsync(() => _commands.UpdateAsync(task, expectedVersion, cancellationToken), cancellationToken);

    public Task<OperationResult> CompleteAsync(
        DownloadTask task,
        DownloadHistoryRecord history,
        long expectedVersion,
        CancellationToken cancellationToken) =>
        ExecuteAsync(() => _commands
            .CompleteAsync(task, history, expectedVersion, cancellationToken), cancellationToken);

    public Task<DownloadTask?> FindAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken) =>
        ExecuteAsync(() => _queries.FindAsync(taskId, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<DownloadTask>> GetUnfinishedAsync(CancellationToken cancellationToken) =>
        ExecuteAsync(() => _queries.GetUnfinishedAsync(cancellationToken), cancellationToken);

    public Task<IReadOnlyList<DownloadTask>> GetUnfinishedByCidAsync(
        long cid,
        CancellationToken cancellationToken) =>
        ExecuteAsync(() => _queries.GetUnfinishedByCidAsync(cid, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<DownloadTask>> GetUnfinishedByCidsAsync(
        IReadOnlyCollection<long> cids,
        CancellationToken cancellationToken) =>
        ExecuteAsync(() => _queries.GetUnfinishedByCidsAsync(cids, cancellationToken), cancellationToken);

    public Task<bool> IsOutputPathReservedAsync(
        string basePath,
        bool ignoreCase,
        CancellationToken cancellationToken) =>
        ExecuteAsync(() => _outputReservations
            .IsOutputPathReservedAsync(basePath, ignoreCase, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<string>> GetActiveOutputReservationKeysAsync(
        bool ignoreCase,
        CancellationToken cancellationToken) =>
        ExecuteAsync(() => _outputReservations
            .GetActiveOutputReservationKeysAsync(ignoreCase, cancellationToken), cancellationToken);

    public Task<bool> IsOutputDirectoryReservedAsync(
        string directory,
        bool ignoreCase,
        CancellationToken cancellationToken) =>
        ExecuteAsync(() => _outputReservations
            .IsOutputDirectoryReservedAsync(directory, ignoreCase, cancellationToken), cancellationToken);

    public Task<bool> HasOutputClaimConflictAsync(
        string basePath,
        long requestedCid,
        DownloadActionClaims requestedClaims,
        bool ignoreCase,
        CancellationToken cancellationToken) =>
        ExecuteAsync(() => _outputReservations
            .HasOutputClaimConflictAsync(
                basePath,
                requestedCid,
                requestedClaims,
                ignoreCase,
                cancellationToken), cancellationToken);

    public Task<DownloadHistoryPage> GetHistoryPageAsync(
        DownloadHistoryCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken,
        DownloadFinishedSort sort = DownloadFinishedSort.DownloadDesc) =>
        ExecuteAsync(() => _queries.GetHistoryPageAsync(cursor, pageSize, cancellationToken, sort), cancellationToken);

    public Task<IReadOnlyList<DownloadHistoryRecord>> GetHistoryByCidAsync(
        long cid,
        CancellationToken cancellationToken) =>
        ExecuteAsync(() => _queries.GetHistoryByCidAsync(cid, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<DownloadHistoryRecord>> GetHistoryByCidsAsync(
        IReadOnlyCollection<long> cids,
        CancellationToken cancellationToken) =>
        ExecuteAsync(() => _queries.GetHistoryByCidsAsync(cids, cancellationToken), cancellationToken);

    public Task<OperationResult> DeleteAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken) =>
        ExecuteAsync(() => _commands.DeleteAsync(taskId, cancellationToken), cancellationToken);

    public Task<OperationResult> DeleteHistoryAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken) =>
        ExecuteAsync(() => _commands.DeleteHistoryAsync(taskId, cancellationToken), cancellationToken);

    public Task<OperationResult> ClearHistoryAsync(CancellationToken cancellationToken) =>
        ExecuteAsync(() => _commands.ClearHistoryAsync(cancellationToken), cancellationToken);

    public Task<IReadOnlyList<QuarantinedDownloadRecord>> GetQuarantinedRecordsAsync(
        CancellationToken cancellationToken) =>
        ExecuteAsync(() => _quarantine.GetRecordsAsync(cancellationToken), cancellationToken);

    public void Dispose() => _database.Dispose();

    private static bool CanExecuteInline =>
        Thread.CurrentThread.IsThreadPoolThread && SynchronizationContext.Current == null;

    private static async Task ExecuteAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await (CanExecuteInline ? operation() : Task.Run(operation, cancellationToken)).ConfigureAwait(false);
    }

    private static async Task<T> ExecuteAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await (CanExecuteInline ? operation() : Task.Run(operation, cancellationToken)).ConfigureAwait(false);
    }
}
