using DownKyi.Application.Downloads;
using DownKyi.Application.Time;
using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Downloads;

internal static class DownloadStoreSchema
{
    public const int CurrentVersion = 10;

    public static async Task InitializeAsync(
        SqliteConnection connection,
        string databasePath,
        bool databaseExisted,
        IClock clock,
        IPhysicalOutputPathResolver physicalOutputPathResolver,
        CancellationToken cancellationToken)
    {
        var format = await LegacyDownloadStoreFormatDetector
            .DetectAsync(connection, databaseExisted, cancellationToken)
            .ConfigureAwait(false);
        if (format.IsCurrent)
        {
            return;
        }

        if (format.DatabaseExisted)
        {
            await DownloadStoreSchemaLifecycle
                .BackupAsync(connection, databasePath, format.UserVersion, clock, cancellationToken)
                .ConfigureAwait(false);
        }

        using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            if (format.IsNew)
            {
                await CurrentDownloadStoreWriter
                    .CreateAsync(connection, transaction, clock.UtcNow, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (format.IsPreviousCurrent)
            {
                await CurrentDownloadStoreWriter
                    .UpgradeHistoryContentAsync(
                        connection,
                        transaction,
                        clock.UtcNow,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                var snapshot = await LegacyDownloadStoreReader
                    .ReadAsync(connection, transaction, format, cancellationToken)
                    .ConfigureAwait(false);
                var plan = LegacyDownloadStoreNormalizer.Normalize(
                    snapshot,
                    physicalOutputPathResolver);
                if (format.RequiresLegacyHistoryRecovery)
                {
                    if (format.IsPreviousCurrentWithLegacyHistory)
                    {
                        await CurrentDownloadStoreWriter
                            .AddHistoryContentColumnAsync(connection, transaction, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    await CurrentDownloadStoreWriter
                        .RecoverCurrentWithLegacyHistoryAsync(
                            connection,
                            transaction,
                            plan,
                            clock.UtcNow,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await CurrentDownloadStoreWriter
                        .UpgradeAsync(
                            connection,
                            transaction,
                            format,
                            plan,
                            clock.UtcNow,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}
