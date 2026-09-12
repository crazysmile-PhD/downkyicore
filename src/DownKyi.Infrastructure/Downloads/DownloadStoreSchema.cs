using DownKyi.Application.Downloads;
using DownKyi.Application.Time;
using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Downloads;

internal static class DownloadStoreSchema
{
    public const int CurrentVersion = 7;

    public static async Task InitializeAsync(
        SqliteConnection connection,
        string databasePath,
        bool databaseExisted,
        IClock clock,
        IPhysicalOutputPathResolver physicalOutputPathResolver,
        CancellationToken cancellationToken)
    {
        var currentVersion = await DownloadStoreSchemaLifecycle
            .ReadUserVersionAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        if (currentVersion > CurrentVersion)
        {
            throw new InvalidOperationException(
                $"Download database schema {currentVersion} is newer than supported schema {CurrentVersion}.");
        }

        if (databaseExisted && currentVersion < CurrentVersion)
        {
            await DownloadStoreSchemaLifecycle
                .BackupAsync(connection, databasePath, currentVersion, clock, cancellationToken)
                .ConfigureAwait(false);
        }

        if (currentVersion == CurrentVersion)
        {
            return;
        }

        using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            if (currentVersion < 1)
            {
                await DownloadStoreSchemaV1Migration
                    .ApplyAsync(connection, transaction, clock.UtcNow, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (currentVersion < 2)
            {
                await DownloadStoreSchemaV2Migration
                    .ApplyAsync(connection, transaction, clock.UtcNow, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (currentVersion < 3)
            {
                await DownloadStoreSchemaV3Migration
                    .ApplyAsync(connection, transaction, clock.UtcNow, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (currentVersion < 4)
            {
                await DownloadStoreSchemaV4Migration
                    .ApplyAsync(
                        connection,
                        transaction,
                        clock.UtcNow,
                        physicalOutputPathResolver,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (currentVersion < 5)
            {
                await DownloadStoreSchemaV5Migration
                    .ApplyAsync(connection, transaction, clock.UtcNow, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (currentVersion < 6)
            {
                await DownloadStoreSchemaV6Migration
                    .ApplyAsync(connection, transaction, clock.UtcNow, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (currentVersion < 7)
            {
                await DownloadStoreSchemaV7Migration
                    .ApplyAsync(connection, transaction, clock.UtcNow, cancellationToken)
                    .ConfigureAwait(false);
            }

            await DownloadStoreSchemaLifecycle
                .SetUserVersionAsync(connection, transaction, CurrentVersion, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}
