using System.Collections.Immutable;
using DownKyi.Application.Downloads;
using DownKyi.Application.Time;
using DownKyi.Domain.Downloads;
using DownKyi.Infrastructure.Downloads;
using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Tests;

public sealed class SqliteDownloadStoreReservationRekeyTests : IDisposable
{
    private readonly SqliteDownloadStoreFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task ReopenedForeignPolicyReservationAgreesAcrossPointSnapshotAndAdd()
    {
        var ignoreCase = DownloadOutputPathKey.UsesCaseInsensitiveComparison;
        var original = Path.Combine(_fixture.TempDirectory, "cafe\u0301-foreign");
        var equivalent = Path.Combine(
            _fixture.TempDirectory,
            ignoreCase ? "CAF\u00c9-FOREIGN" : "caf\u00e9-foreign");
        using (var first = _fixture.CreateStore())
        {
            Assert.True((await first.AddAsync(
                _fixture.CreateQueuedTask("foreign-policy-original", original),
                TestContext.Current.CancellationToken)).IsSuccess);
        }

        using (var connection = await _fixture.OpenConnectionAsync(readOnly: false))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE download_base SET output_reservation_key = @foreign_key
                WHERE id = 'foreign-policy-original'
                """;
            command.Parameters.AddWithValue(
                "@foreign_key",
                DownloadOutputPathKey.Create(original, !ignoreCase));
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        using var reopened = _fixture.CreateStore();
        await reopened.InitializeAsync(TestContext.Current.CancellationToken);
        var key = DownloadOutputPathKey.Create(equivalent, ignoreCase);
        Assert.Contains(key, await reopened.GetActiveOutputReservationKeysAsync(
            ignoreCase, TestContext.Current.CancellationToken));
        Assert.True(await reopened.IsOutputPathReservedAsync(
            equivalent, ignoreCase, TestContext.Current.CancellationToken));
        Assert.False((await reopened.AddAsync(
            _fixture.CreateQueuedTask("foreign-policy-second", equivalent),
            TestContext.Current.CancellationToken)).IsSuccess);
        var alternatePolicy = !ignoreCase;
        var alternateEquivalent = Path.Combine(_fixture.TempDirectory,
            ignoreCase ? "caf\u00e9-foreign" : "CAF\u00c9-FOREIGN");
        var alternateKey = DownloadOutputPathKey.Create(alternateEquivalent, alternatePolicy);
        Assert.Contains(alternateKey, await reopened.GetActiveOutputReservationKeysAsync(
            alternatePolicy, TestContext.Current.CancellationToken));
        Assert.True(await reopened.IsOutputPathReservedAsync(alternateEquivalent,
            alternatePolicy, TestContext.Current.CancellationToken));
        if (ignoreCase)
        {
            var distinctUnderAlternatePolicy = Path.Combine(_fixture.TempDirectory, "CAF\u00c9-FOREIGN");
            Assert.False(await reopened.IsOutputPathReservedAsync(distinctUnderAlternatePolicy,
                alternatePolicy, TestContext.Current.CancellationToken));
        }
        Assert.Equal(DownloadOutputPathKey.Create(original, ignoreCase),
            await _fixture.ReadReservationKeyAsync("foreign-policy-original"));
        var backupPath = Assert.Single(Directory.GetFiles(Path.Combine(_fixture.TempDirectory, "Backup"),
            "download.db.reservation-keys-*.bak"));
        using var backup = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await backup.OpenAsync(TestContext.Current.CancellationToken);
        using var backupKey = backup.CreateCommand();
        backupKey.CommandText = """
            SELECT output_reservation_key FROM download_base
            WHERE id = 'foreign-policy-original'
            """;
        Assert.Equal(DownloadOutputPathKey.Create(original, !ignoreCase),
            await backupKey.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReservationRekeyRepairsLegacyNullAndIsIdempotentAcrossReopenAndPolicyChanges()
    {
        var ignoreCase = DownloadOutputPathKey.UsesCaseInsensitiveComparison;
        var original = Path.Combine(_fixture.TempDirectory, "foreign-cafe\u0301");
        var legacy = Path.Combine(_fixture.TempDirectory, "legacy");
        using (var first = _fixture.CreateStore())
        {
            var pending = _fixture.CreatePausedTask("rekey-original", original);
            pending = pending.UpdateOutput(new DownloadOutput(original, "1 GB",
                new Dictionary<string, string> { ["cover"] = "cover.jpg" },
                pending.Output.StagingToken,
                new DownloadPublishingArtifact("media", "video.mp4", 3,
                    new string('A', 64))), _fixture.Clock.UtcNow.AddSeconds(6)).RequireValue();
            Assert.True((await first.AddAsync(pending,
                TestContext.Current.CancellationToken)).IsSuccess);
            Assert.True((await first.AddAsync(_fixture.CreatePausedTask("rekey-legacy", legacy),
                TestContext.Current.CancellationToken)).IsSuccess);
            Assert.True((await first.AddHistoryAsync(DownloadHistoryRecord.FromCompletedTask(
                    _fixture.CreateCompletedTask("rekey-history", 123)),
                TestContext.Current.CancellationToken)).IsSuccess);
        }

        var originalBefore = await _fixture.ReadStoredStateAsync("rekey-original");
        var legacyBefore = await _fixture.ReadStoredStateAsync("rekey-legacy");
        DownloadHistoryRecord historyBefore;
        using (var historyReader = _fixture.CreateStore())
        {
            historyBefore = Assert.Single((await historyReader.GetHistoryPageAsync(
                null, 10, TestContext.Current.CancellationToken)).Items);
        }
        var publicationBefore = await _fixture.ReadPublicationPayloadAsync("rekey-original");
        await _fixture.SetReservationKeyAsync("rekey-original",
            DownloadOutputPathKey.Create(original, !ignoreCase));
        await _fixture.SetReservationKeyAsync("rekey-legacy", null);
        DownloadHistoryRecord historyAfter;
        using (var reopened = _fixture.CreateStore())
        {
            await reopened.InitializeAsync(TestContext.Current.CancellationToken);
            historyAfter = Assert.Single((await reopened.GetHistoryPageAsync(
                null, 10, TestContext.Current.CancellationToken)).Items);
        }

        Assert.Equal(DownloadOutputPathKey.Create(original, ignoreCase),
            await _fixture.ReadReservationKeyAsync("rekey-original"));
        Assert.Equal(DownloadOutputPathKey.Create(legacy, ignoreCase),
            await _fixture.ReadReservationKeyAsync("rekey-legacy"));
        Assert.Equal(originalBefore, await _fixture.ReadStoredStateAsync("rekey-original"));
        Assert.Equal(legacyBefore, await _fixture.ReadStoredStateAsync("rekey-legacy"));
        Assert.Equivalent(historyBefore, historyAfter, strict: true);
        Assert.Equal(publicationBefore, await _fixture.ReadPublicationPayloadAsync("rekey-original"));
        using (var repeated = _fixture.CreateStore())
        {
            await repeated.InitializeAsync(TestContext.Current.CancellationToken);
        }
        Assert.Single(Directory.GetFiles(Path.Combine(_fixture.TempDirectory, "Backup"),
            "download.db.reservation-keys-*.bak"));

        await _fixture.SetReservationKeyAsync("rekey-original",
            DownloadOutputPathKey.Create(original, !ignoreCase));
        using (var switched = _fixture.CreateStore())
        {
            await switched.InitializeAsync(TestContext.Current.CancellationToken);
        }
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_fixture.TempDirectory, "Backup"),
            "download.db.reservation-keys-*.bak").Length);
    }

    [Fact]
    public async Task CanonicalCollisionBlocksStoreAndDirectAddWithoutChangingAnyTask()
    {
        var composed = Path.Combine(_fixture.TempDirectory, "caf\u00e9-conflict");
        var decomposed = Path.Combine(_fixture.TempDirectory, "cafe\u0301-conflict");
        using (var first = _fixture.CreateStore())
        {
            Assert.True((await first.AddAsync(_fixture.CreatePausedTask("collision-a", composed),
                TestContext.Current.CancellationToken)).IsSuccess);
            Assert.True((await first.AddAsync(_fixture.CreatePausedTask("collision-b"),
                TestContext.Current.CancellationToken)).IsSuccess);
        }

        await _fixture.SetPathAndReservationKeyAsync("collision-b", decomposed,
            DownloadOutputPathKey.Create(decomposed,
                !DownloadOutputPathKey.UsesCaseInsensitiveComparison));
        var firstBefore = await _fixture.ReadStoredStateAsync("collision-a");
        var secondBefore = await _fixture.ReadStoredStateAsync("collision-b");
        var firstKey = await _fixture.ReadReservationKeyAsync("collision-a");
        var secondKey = await _fixture.ReadReservationKeyAsync("collision-b");
        using var reopened = _fixture.CreateStore();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reopened.InitializeAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reopened.AddAsync(_fixture.CreateQueuedTask("collision-third",
                Path.Combine(_fixture.TempDirectory, "unrelated")), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reopened.GetUnfinishedAsync(TestContext.Current.CancellationToken));
        Assert.Equal(firstBefore, await _fixture.ReadStoredStateAsync("collision-a"));
        Assert.Equal(secondBefore, await _fixture.ReadStoredStateAsync("collision-b"));
        Assert.Equal(firstKey, await _fixture.ReadReservationKeyAsync("collision-a"));
        Assert.Equal(secondKey, await _fixture.ReadReservationKeyAsync("collision-b"));
        Assert.False(Directory.Exists(Path.Combine(_fixture.TempDirectory, "Backup")));
    }

    [Fact]
    public async Task ReservationRekeyPreflightsQuarantinedUniqueOccupants()
    {
        var target = Path.Combine(_fixture.TempDirectory, "reserved-target");
        using (var first = _fixture.CreateStore())
        {
            Assert.True((await first.AddAsync(_fixture.CreatePausedTask("occupied-quarantine", target),
                TestContext.Current.CancellationToken)).IsSuccess);
            Assert.True((await first.AddAsync(_fixture.CreatePausedTask("moving", Path.Combine(_fixture.TempDirectory, "old")),
                TestContext.Current.CancellationToken)).IsSuccess);
        }
        await _fixture.InsertPreexistingQuarantineAsync("occupied-quarantine");
        await _fixture.SetPathAndReservationKeyAsync("moving", target,
            DownloadOutputPathKey.Create(Path.Combine(_fixture.TempDirectory, "old"),
                DownloadOutputPathKey.UsesCaseInsensitiveComparison));
        using (var reopened = _fixture.CreateStore())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                reopened.InitializeAsync(TestContext.Current.CancellationToken));
        }

    }

    [Fact]
    public async Task QuarantinedRowsRetainNullOrForeignKeysWithoutBlockingUnrelatedRecovery()
    {
        var foreignPath = OperatingSystem.IsWindows() ? "/foreign/quarantined" : @"C:\foreign\quarantined";
        var quarantinedPath = Path.Combine(_fixture.TempDirectory, "quarantined-foreign-key");
        using (var first = _fixture.CreateStore())
        {
            Assert.True((await first.AddAsync(_fixture.CreatePausedTask("quarantined-null"),
                TestContext.Current.CancellationToken)).IsSuccess);
            Assert.True((await first.AddAsync(_fixture.CreatePausedTask("quarantined-foreign",
                quarantinedPath), TestContext.Current.CancellationToken)).IsSuccess);
            Assert.True((await first.AddAsync(_fixture.CreatePausedTask("safe-unfinished"),
                TestContext.Current.CancellationToken)).IsSuccess);
        }
        await _fixture.InsertPreexistingQuarantineAsync("quarantined-null");
        await _fixture.InsertPreexistingQuarantineAsync("quarantined-foreign");
        await _fixture.SetPathAndReservationKeyAsync("quarantined-null", foreignPath, null);
        var foreignKey = DownloadOutputPathKey.Create(quarantinedPath,
            !DownloadOutputPathKey.UsesCaseInsensitiveComparison);
        await _fixture.SetReservationKeyAsync("quarantined-foreign", foreignKey);

        using var reopened = _fixture.CreateStore();
        await reopened.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal("safe-unfinished", Assert.Single(await reopened.GetUnfinishedAsync(
            TestContext.Current.CancellationToken)).Id.Value);
        Assert.Equal(2, (await reopened.GetQuarantinedRecordsAsync(
            TestContext.Current.CancellationToken)).Count);
        Assert.Null(await _fixture.ReadReservationKeyAsync("quarantined-null"));
        Assert.Equal(foreignKey, await _fixture.ReadReservationKeyAsync("quarantined-foreign"));
        Assert.Equal(foreignPath, (await _fixture.ReadStoredStateAsync("quarantined-null")).Path);
        Assert.True((await reopened.AddAsync(_fixture.CreateQueuedTask("safe-new",
            Path.Combine(_fixture.TempDirectory, "safe-new")), TestContext.Current.CancellationToken)).IsSuccess);
        Assert.False(Directory.Exists(Path.Combine(_fixture.TempDirectory, "Backup")));
    }

    [Fact]
    public async Task ReservationRekeySqlFailureRollsBackAllKeysAndKeepsPrechangeBackup()
    {
        var firstPath = Path.Combine(_fixture.TempDirectory, "rollback-a");
        var secondPath = Path.Combine(_fixture.TempDirectory, "rollback-b");
        using (var first = _fixture.CreateStore())
        {
            Assert.True((await first.AddAsync(_fixture.CreatePausedTask("rollback-a", firstPath),
                TestContext.Current.CancellationToken)).IsSuccess);
            Assert.True((await first.AddAsync(_fixture.CreatePausedTask("rollback-b", secondPath),
                TestContext.Current.CancellationToken)).IsSuccess);
        }
        var firstForeign = DownloadOutputPathKey.Create(firstPath,
            !DownloadOutputPathKey.UsesCaseInsensitiveComparison);
        var secondForeign = DownloadOutputPathKey.Create(secondPath,
            !DownloadOutputPathKey.UsesCaseInsensitiveComparison);
        await _fixture.SetReservationKeyAsync("rollback-a", firstForeign);
        await _fixture.SetReservationKeyAsync("rollback-b", secondForeign);
        using (var connection = await _fixture.OpenConnectionAsync(readOnly: false))
        using (var trigger = connection.CreateCommand())
        {
            trigger.CommandText = """
                CREATE TRIGGER reject_second_rekey BEFORE UPDATE OF output_reservation_key
                ON download_base WHEN NEW.id = 'rollback-b' AND NEW.output_reservation_key IS NOT NULL
                BEGIN SELECT RAISE(ABORT, 'synthetic rekey failure'); END;
                """;
            await trigger.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        using var reopened = _fixture.CreateStore();
        await Assert.ThrowsAsync<SqliteException>(() =>
            reopened.InitializeAsync(TestContext.Current.CancellationToken));
        Assert.Equal(firstForeign, await _fixture.ReadReservationKeyAsync("rollback-a"));
        Assert.Equal(secondForeign, await _fixture.ReadReservationKeyAsync("rollback-b"));
        Assert.Single(Directory.GetFiles(Path.Combine(_fixture.TempDirectory, "Backup"),
            "download.db.reservation-keys-*.bak"));
    }

    [Fact]
    public async Task ReservationRekeyHandlesKeySwapWithoutDroppingUniqueIndex()
    {
        var firstPath = Path.Combine(_fixture.TempDirectory, "swap-a");
        var secondPath = Path.Combine(_fixture.TempDirectory, "swap-b");
        using (var store = _fixture.CreateStore())
        {
            Assert.True((await store.AddAsync(_fixture.CreateQueuedTask("swap-a", firstPath),
                TestContext.Current.CancellationToken)).IsSuccess);
            Assert.True((await store.AddAsync(_fixture.CreateQueuedTask("swap-b", secondPath),
                TestContext.Current.CancellationToken)).IsSuccess);
        }
        var firstKey = DownloadOutputPathKey.Create(firstPath,
            DownloadOutputPathKey.UsesCaseInsensitiveComparison);
        var secondKey = DownloadOutputPathKey.Create(secondPath,
            DownloadOutputPathKey.UsesCaseInsensitiveComparison);
        await _fixture.SetReservationKeyAsync("swap-a", null);
        await _fixture.SetReservationKeyAsync("swap-b", null);
        await _fixture.SetReservationKeyAsync("swap-a", secondKey);
        await _fixture.SetReservationKeyAsync("swap-b", firstKey);

        using var reopened = _fixture.CreateStore();
        await reopened.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(firstKey, await _fixture.ReadReservationKeyAsync("swap-a"));
        Assert.Equal(secondKey, await _fixture.ReadReservationKeyAsync("swap-b"));
        using var connection = await _fixture.OpenReadOnlyConnectionAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM sqlite_master
            WHERE type = 'index' AND name = 'ux_download_base_output_reservation'
            """;
        Assert.Equal(1L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReservationRekeyCancellationAndBackupFailureLeaveOriginalKeyUntouched()
    {
        var path = Path.Combine(_fixture.TempDirectory, "backup-failure");
        using (var first = _fixture.CreateStore())
        {
            Assert.True((await first.AddAsync(_fixture.CreatePausedTask("backup-failure", path),
                TestContext.Current.CancellationToken)).IsSuccess);
        }
        var foreign = DownloadOutputPathKey.Create(path,
            !DownloadOutputPathKey.UsesCaseInsensitiveComparison);
        await _fixture.SetReservationKeyAsync("backup-failure", foreign);

        using var cancellation = new CancellationTokenSource();
        using (var canceled = _fixture.CreateStore(clock: new SqliteDownloadStoreFixture.CancelOnReadClock(_fixture.Clock.UtcNow, cancellation)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                canceled.InitializeAsync(cancellation.Token));
        }
        Assert.Equal(foreign, await _fixture.ReadReservationKeyAsync("backup-failure"));

        var backupDirectory = Path.Combine(_fixture.TempDirectory, "Backup");
        Directory.Delete(backupDirectory);
        await File.WriteAllTextAsync(backupDirectory, "prevent backup directory creation",
            TestContext.Current.CancellationToken);
        using (var failedBackup = _fixture.CreateStore())
        {
            await Assert.ThrowsAnyAsync<IOException>(() =>
                failedBackup.InitializeAsync(TestContext.Current.CancellationToken));
        }
        Assert.Equal(foreign, await _fixture.ReadReservationKeyAsync("backup-failure"));
        File.Delete(backupDirectory);

        using var reopened = _fixture.CreateStore();
        await reopened.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DownloadOutputPathKey.Create(path,
            DownloadOutputPathKey.UsesCaseInsensitiveComparison),
            await _fixture.ReadReservationKeyAsync("backup-failure"));
    }
}
