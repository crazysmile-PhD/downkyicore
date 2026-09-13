using DownKyi.Application.Downloads;
using DownKyi.Application.Time;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Infrastructure.Downloads;

public sealed class SqliteDownloadTaskStore : IDownloadTaskStore, IDisposable
{
    private readonly SqliteDownloadStoreDatabase _database;
    private readonly SqliteDownloadStoreQueries _queries;
    private readonly SqliteDownloadStoreCommands _commands;
    private readonly SqliteDownloadStoreOutputReservations _outputReservations;
    private readonly SqliteDownloadStoreQuarantine _quarantine;

    public SqliteDownloadTaskStore(SqliteDownloadTaskStoreOptions options, IClock clock)
        : this(
            options,
            clock,
            new FileSystemPhysicalOutputPathResolver(),
            NullLogger<SqliteDownloadTaskStore>.Instance)
    {
    }

    public SqliteDownloadTaskStore(
        SqliteDownloadTaskStoreOptions options,
        IClock clock,
        IPhysicalOutputPathResolver physicalOutputPathResolver)
        : this(options, clock, physicalOutputPathResolver, NullLogger<SqliteDownloadTaskStore>.Instance)
    {
    }

    public SqliteDownloadTaskStore(
        SqliteDownloadTaskStoreOptions options,
        IClock clock,
        ILogger<SqliteDownloadTaskStore> logger)
        : this(options, clock, new FileSystemPhysicalOutputPathResolver(), logger)
    {
    }

    public SqliteDownloadTaskStore(
        SqliteDownloadTaskStoreOptions options,
        IClock clock,
        IPhysicalOutputPathResolver physicalOutputPathResolver,
        ILogger<SqliteDownloadTaskStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(physicalOutputPathResolver);
        ArgumentNullException.ThrowIfNull(logger);
        if (options.BusyTimeout <= TimeSpan.Zero || options.BusyTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        _database = new SqliteDownloadStoreDatabase(
            options,
            clock,
            physicalOutputPathResolver,
            logger,
            typeof(SqliteDownloadTaskStore));
        _quarantine = new SqliteDownloadStoreQuarantine(_database);
        _queries = new SqliteDownloadStoreQueries(_database, clock);
        _commands = new SqliteDownloadStoreCommands(_database);
        _outputReservations = new SqliteDownloadStoreOutputReservations(_database);
    }

    public Task InitializeAsync(CancellationToken cancellationToken) =>
        _database.InitializeAsync(cancellationToken);

    public async Task<OperationResult> AddAsync(DownloadTask task, CancellationToken cancellationToken) =>
        await _outputReservations.AddAsync(task, cancellationToken).ConfigureAwait(false);

    public async Task<OperationResult> UpdateAsync(
        DownloadTask task,
        long expectedVersion,
        CancellationToken cancellationToken) =>
        await _commands.UpdateAsync(task, expectedVersion, cancellationToken).ConfigureAwait(false);

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

    public async Task<DownloadHistoryPage> GetHistoryPageAsync(
        DownloadHistoryCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken) =>
        await _queries.GetHistoryPageAsync(cursor, pageSize, cancellationToken).ConfigureAwait(false);

    public async Task<OperationResult> DeleteAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken) =>
        await _commands.DeleteAsync(taskId, cancellationToken).ConfigureAwait(false);

    public async Task<OperationResult> ClearHistoryAsync(CancellationToken cancellationToken) =>
        await _commands.ClearHistoryAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<QuarantinedDownloadRecord>> GetQuarantinedRecordsAsync(
        CancellationToken cancellationToken) =>
        await _quarantine.GetRecordsAsync(cancellationToken).ConfigureAwait(false);

    public async Task<bool> IsLegacyUpgradeAdmissionBlockedAsync(
        CancellationToken cancellationToken) =>
        await _quarantine.IsLegacyUpgradeAdmissionBlockedAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<OperationResult> ConfirmLegacyRemoteTasksStoppedAsync(
        CancellationToken cancellationToken) =>
        await _quarantine.ConfirmLegacyRemoteTasksStoppedAsync(cancellationToken)
            .ConfigureAwait(false);

    public void Dispose() => _database.Dispose();
}
