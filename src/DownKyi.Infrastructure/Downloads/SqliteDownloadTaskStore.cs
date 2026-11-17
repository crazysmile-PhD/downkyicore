using DownKyi.Application.Downloads;
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

    public Task InitializeAsync(CancellationToken cancellationToken) =>
        _database.InitializeAsync(cancellationToken);

    public Task BackupAndResetAsync(CancellationToken cancellationToken) =>
        _database.BackupAndResetAsync(cancellationToken);

    public async Task<OperationResult> AddAsync(DownloadTask task, CancellationToken cancellationToken) =>
        await _outputReservations.AddAsync(task, cancellationToken).ConfigureAwait(false);

    public async Task<OperationResult> AddHistoryAsync(
        DownloadHistoryRecord history,
        CancellationToken cancellationToken) =>
        await _commands.AddHistoryAsync(history, cancellationToken).ConfigureAwait(false);

    public async Task<OperationResult> UpdateAsync(
        DownloadTask task,
        long expectedVersion,
        CancellationToken cancellationToken) =>
        await _commands.UpdateAsync(task, expectedVersion, cancellationToken).ConfigureAwait(false);

    public async Task<OperationResult> CompleteAsync(
        DownloadTask task,
        DownloadHistoryRecord history,
        long expectedVersion,
        CancellationToken cancellationToken) =>
        await _commands
            .CompleteAsync(task, history, expectedVersion, cancellationToken)
            .ConfigureAwait(false);

    public async Task<OperationResult> UpdateProgressAsync(
        DownloadProgressWrite progressWrite,
        CancellationToken cancellationToken) =>
        await _commands.UpdateProgressAsync(progressWrite, cancellationToken).ConfigureAwait(false);

    public async Task<DownloadTask?> FindAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken) =>
        await _queries.FindAsync(taskId, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<DownloadTask>> GetUnfinishedAsync(CancellationToken cancellationToken) =>
        await _queries.GetUnfinishedAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<DownloadTask>> GetUnfinishedByCidAsync(
        long cid,
        CancellationToken cancellationToken) =>
        await _queries.GetUnfinishedByCidAsync(cid, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<DownloadTask>> GetUnfinishedByCidsAsync(
        IReadOnlyCollection<long> cids,
        CancellationToken cancellationToken) =>
        await _queries.GetUnfinishedByCidsAsync(cids, cancellationToken).ConfigureAwait(false);

    public async Task<bool> IsOutputPathReservedAsync(
        string basePath,
        bool ignoreCase,
        CancellationToken cancellationToken) =>
        await _outputReservations
            .IsOutputPathReservedAsync(basePath, ignoreCase, cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<string>> GetActiveOutputReservationKeysAsync(
        bool ignoreCase,
        CancellationToken cancellationToken) =>
        await _outputReservations
            .GetActiveOutputReservationKeysAsync(ignoreCase, cancellationToken)
            .ConfigureAwait(false);

    public async Task<bool> IsOutputDirectoryReservedAsync(
        string directory,
        bool ignoreCase,
        CancellationToken cancellationToken) =>
        await _outputReservations
            .IsOutputDirectoryReservedAsync(directory, ignoreCase, cancellationToken)
            .ConfigureAwait(false);

    public async Task<bool> HasOutputClaimConflictAsync(
        string basePath,
        long requestedCid,
        DownloadActionClaims requestedClaims,
        bool ignoreCase,
        CancellationToken cancellationToken) =>
        await _outputReservations
            .HasOutputClaimConflictAsync(
                basePath,
                requestedCid,
                requestedClaims,
                ignoreCase,
                cancellationToken)
            .ConfigureAwait(false);

    public async Task<DownloadHistoryPage> GetHistoryPageAsync(
        DownloadHistoryCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken) =>
        await _queries.GetHistoryPageAsync(cursor, pageSize, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<DownloadHistoryRecord>> GetHistoryByCidAsync(
        long cid,
        CancellationToken cancellationToken) =>
        await _queries.GetHistoryByCidAsync(cid, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<DownloadHistoryRecord>> GetHistoryByCidsAsync(
        IReadOnlyCollection<long> cids,
        CancellationToken cancellationToken) =>
        await _queries.GetHistoryByCidsAsync(cids, cancellationToken).ConfigureAwait(false);

    public async Task<OperationResult> DeleteAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken) =>
        await _commands.DeleteAsync(taskId, cancellationToken).ConfigureAwait(false);

    public async Task<OperationResult> DeleteHistoryAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken) =>
        await _commands.DeleteHistoryAsync(taskId, cancellationToken).ConfigureAwait(false);

    public async Task<OperationResult> ClearHistoryAsync(CancellationToken cancellationToken) =>
        await _commands.ClearHistoryAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<QuarantinedDownloadRecord>> GetQuarantinedRecordsAsync(
        CancellationToken cancellationToken) =>
        await _quarantine.GetRecordsAsync(cancellationToken).ConfigureAwait(false);

    public void Dispose() => _database.Dispose();
}
