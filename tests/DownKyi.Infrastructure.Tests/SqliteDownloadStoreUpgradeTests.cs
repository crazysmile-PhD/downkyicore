using System.Collections.Immutable;
using DownKyi.Application.Downloads;
using DownKyi.Application.Time;
using DownKyi.Domain.Downloads;
using DownKyi.Infrastructure.Downloads;
using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Tests;

public sealed class SqliteDownloadStoreUpgradeTests : IDisposable
{
    private readonly SqliteDownloadStoreFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task InitializeCreatesCurrentSchema()
    {
        using var store = _fixture.CreateStore();

        await store.InitializeAsync(TestContext.Current.CancellationToken);

        using var connection = await _fixture.OpenReadOnlyConnectionAsync().ConfigureAwait(true);
        using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        Assert.Equal((long)DownloadStoreSchema.CurrentVersion, await version.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        Assert.True(await _fixture.TableExistsAsync("download_history"));
        Assert.False(await _fixture.TableExistsAsync("downloaded"));
        Assert.Equal(
            [
                "id", "cid", "zone_id", "order", "main_title", "name", "duration",
                "video_codec_name", "resolution", "audio_codec", "file_size",
                "published_artifacts", "finished_timestamp", "finished_time", "max_speed_display", "requested_content"
            ],
            await _fixture.ReadTableColumnsAsync("download_history"));
        Assert.True(await _fixture.TableExistsAsync("download_upgrade_admission_gate"));
        Assert.Equal(0, await _fixture.CountSchemaMigrationAsync(4));
        Assert.Equal(0, await _fixture.CountSchemaMigrationAsync(5));
        Assert.Equal(0, await _fixture.CountSchemaMigrationAsync(8));
        Assert.Equal(1, await _fixture.CountSchemaMigrationAsync(DownloadStoreSchema.CurrentVersion));
        Assert.False(await store.IsLegacyUpgradeAdmissionBlockedAsync(
            TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VersionNineHistoryUpgradesWithoutInventingRequestedContent(
        bool withLegacyHistory)
    {
        using (var current = _fixture.CreateStore())
        {
            Assert.True((await current.AddHistoryAsync(
                DownloadHistoryRecord.FromCompletedTask(
                    _fixture.CreateCompletedTask("preexisting-history", 123)),
                TestContext.Current.CancellationToken)).IsSuccess);
        }

        using (var connection = await _fixture.OpenConnectionAsync(readOnly: false))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                ALTER TABLE download_history DROP COLUMN requested_content;
                PRAGMA user_version = 9;
                DELETE FROM download_schema_migrations WHERE version = 10;
                INSERT INTO download_schema_migrations(version, applied_at_utc) VALUES (9, 0);
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        if (withLegacyHistory)
        {
            await _fixture.SimulateLegacyVersionWriteAsync();
        }

        using var upgraded = _fixture.CreateStore();
        await upgraded.InitializeAsync(TestContext.Current.CancellationToken);
        var history = (await upgraded.GetHistoryPageAsync(
            null,
            10,
            TestContext.Current.CancellationToken)).Items;

        Assert.Equal(withLegacyHistory ? 2 : 1, history.Count);
        Assert.All(history, item => Assert.Null(item.RequestedContent));
        Assert.Contains(history, item => item.Id.Value == "preexisting-history");
        if (withLegacyHistory)
        {
            Assert.Contains(history, item => item.Id.Value == "legacy-history");
        }
        Assert.Equal(DownloadStoreSchema.CurrentVersion, await _fixture.ReadSchemaVersionAsync());
        Assert.Contains("requested_content", await _fixture.ReadTableColumnsAsync("download_history"));
        Assert.Single(Directory.GetFiles(
            Path.Combine(_fixture.TempDirectory, "Backup"),
            "download.db.schema-v9-*.bak"));
    }

    [Fact]
    public async Task InitializeTreatsExistingEmptyDatabaseAsNewWithoutLosingBackup()
    {
        Directory.CreateDirectory(_fixture.TempDirectory);
        using (var connection = new SqliteConnection(
                   new SqliteConnectionStringBuilder
                   {
                       DataSource = Path.Combine(_fixture.TempDirectory, "download.db"),
                       Mode = SqliteOpenMode.ReadWriteCreate,
                       Pooling = false
                   }.ToString()))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA application_id = 1";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        using var store = _fixture.CreateStore();
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DownloadStoreSchema.CurrentVersion, await _fixture.ReadSchemaVersionAsync());
        Assert.Equal(1, await _fixture.CountSchemaMigrationAsync(DownloadStoreSchema.CurrentVersion));
        Assert.Single(Directory.GetFiles(
            Path.Combine(_fixture.TempDirectory, "Backup"),
            "download.db.schema-v0-*.bak"));
    }

    [Fact]
    public async Task InitializeBacksUpAndMigratesLegacyDatabaseInPlace()
    {
        await _fixture.CreateLegacyDatabaseAsync();
        using var store = _fixture.CreateStore();

        await store.InitializeAsync(TestContext.Current.CancellationToken);
        var restored = Assert.Single(
            await store.GetUnfinishedAsync(TestContext.Current.CancellationToken));

        Assert.Equal("legacy-resume", restored.Id.Value);
        Assert.Equal(DownloadPhase.Paused, restored.Phase);
        Assert.Equal("aria-gid", restored.Transfer.BackendIdentity);
        Assert.Equal(
            DownloadContentSelection.None with { Video = true },
            restored.Plan.RequestedContent);
        Assert.Equal("video.m4s", restored.Plan.TransferFiles["video"]);
        Assert.Equal("cover", Assert.Single(restored.Transfer.CompletedFileKeys));
        Assert.Equal(42.5, restored.Progress.Percentage);
        Assert.Null(restored.Plan.NfoRequest);
        Assert.Single(Directory.GetFiles(
            Path.Combine(_fixture.TempDirectory, "Backup"),
            "download.db.schema-v0-*.bak"));
    }

    [Fact]
    public async Task InitializeRecoversCurrentSchemaAfterLegacyVersionRecreatesDownloadedTable()
    {
        var currentTask = _fixture.CreatePausedTask("current-active");
        var currentHistory = DownloadHistoryRecord.FromCompletedTask(
            _fixture.CreateCompletedTask("current-history", 123));
        using (var current = _fixture.CreateStore())
        {
            Assert.True((await current.AddAsync(
                currentTask,
                TestContext.Current.CancellationToken)).IsSuccess);
            Assert.True((await current.AddHistoryAsync(
                currentHistory,
                TestContext.Current.CancellationToken)).IsSuccess);
        }

        await _fixture.SimulateLegacyVersionWriteAsync();

        using (var recovered = _fixture.CreateStore())
        {
            await recovered.InitializeAsync(TestContext.Current.CancellationToken);

            var unfinished = await recovered.GetUnfinishedAsync(TestContext.Current.CancellationToken);
            Assert.Equal(
                ["current-active", "legacy-active"],
                unfinished.Select(task => task.Id.Value).Order(StringComparer.Ordinal));
            var legacyActive = Assert.Single(unfinished, task => task.Id.Value == "legacy-active");
            Assert.True(Guid.TryParseExact(legacyActive.Output.StagingToken, "N", out _));

            var history = await recovered.GetHistoryPageAsync(
                null,
                10,
                TestContext.Current.CancellationToken);
            Assert.Equal(
                ["current-history", "legacy-history"],
                history.Items.Select(item => item.Id.Value).Order(StringComparer.Ordinal));
        }

        Assert.Equal(DownloadStoreSchema.CurrentVersion, await _fixture.ReadSchemaVersionAsync());
        Assert.False(await _fixture.TableExistsAsync("downloaded"));
        Assert.Equal(0, await _fixture.CountDownloadBaseRecordAsync("legacy-history"));
        Assert.Single(Directory.GetFiles(
            Path.Combine(_fixture.TempDirectory, "Backup"),
            "download.db.schema-v10-*.bak"));

        using (var reopened = _fixture.CreateStore())
        {
            await reopened.InitializeAsync(TestContext.Current.CancellationToken);
        }

        Assert.Single(Directory.GetFiles(
            Path.Combine(_fixture.TempDirectory, "Backup"),
            "download.db.schema-v10-*.bak"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task InitializeUpgradesEveryRecognizedLegacyFormatDirectlyToCurrent(int version)
    {
        await _fixture.CreateLegacyVersionDatabaseAsync(version);
        using var store = _fixture.CreateStore();

        await store.InitializeAsync(TestContext.Current.CancellationToken);

        var restored = Assert.Single(
            await store.GetUnfinishedAsync(TestContext.Current.CancellationToken));
        Assert.Equal($"legacy-v{version}", restored.Id.Value);
        Assert.Equal(DownloadPhase.Paused, restored.Phase);
        Assert.Equal("aria-gid", restored.Transfer.BackendIdentity);
        Assert.Equal(DownloadStoreSchema.CurrentVersion, await _fixture.ReadSchemaVersionAsync());
        Assert.Equal(1, await _fixture.CountSchemaMigrationAsync(DownloadStoreSchema.CurrentVersion));
        Assert.Single(Directory.GetFiles(
            Path.Combine(_fixture.TempDirectory, "Backup"),
            $"download.db.schema-v{version}-*.bak"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LegacyMigrationKeepsCollisionQuarantineWithItsOwner(
        bool quarantineActive,
        bool quarantineHistory)
    {
        var active = _fixture.CreatePausedTask(
            "legacy-active-history-collision",
            Path.Combine(_fixture.TempDirectory, "collision", "video"));
        await _fixture.CreateVersionThreeDatabaseAsync(active);
        await _fixture.InsertLegacyDownloadedRowAsync(active.Id.Value);
        if (quarantineActive)
        {
            await _fixture.InsertLegacyQuarantineAsync("downloading", active.Id.Value);
        }

        if (quarantineHistory)
        {
            await _fixture.InsertLegacyQuarantineAsync("downloaded", active.Id.Value);
        }

        var before = await _fixture.ReadStoredStateAsync(active.Id.Value);

        using (var first = _fixture.CreateStore())
        {
            await first.InitializeAsync(TestContext.Current.CancellationToken);

            var unfinished = await first.GetUnfinishedAsync(TestContext.Current.CancellationToken);
            if (quarantineActive)
            {
                Assert.Empty(unfinished);
            }
            else
            {
                var restored = Assert.Single(unfinished);
                Assert.Equal(active.Id, restored.Id);
                Assert.Equal(DownloadPhase.Paused, restored.Phase);
            }

            Assert.Empty((await first.GetHistoryPageAsync(
                null,
                10,
                TestContext.Current.CancellationToken)).Items);
            var quarantine = await first.GetQuarantinedRecordsAsync(
                TestContext.Current.CancellationToken);
            if (quarantineActive)
            {
                var activeQuarantine = Assert.Single(quarantine);
                Assert.Equal("downloading", activeQuarantine.SourceTable);
                Assert.Equal(active.Id.Value, activeQuarantine.RecordId);
                Assert.Equal("legacy-downloading-corrupt-record", activeQuarantine.Reason);
            }
            else
            {
                Assert.Empty(quarantine);
            }
        }

        Assert.Equal(before, await _fixture.ReadStoredStateAsync(active.Id.Value));
        Assert.Equal(1, await _fixture.CountDownloadBaseRecordAsync(active.Id.Value));
        Assert.Equal(1, await _fixture.CountDownloadingRecordAsync(active.Id.Value));
        Assert.False(await _fixture.TableExistsAsync("downloaded"));
        Assert.Equal(DownloadStoreSchema.CurrentVersion, await _fixture.ReadSchemaVersionAsync());

        using var reopened = _fixture.CreateStore();
        await reopened.InitializeAsync(TestContext.Current.CancellationToken);
        var reopenedUnfinished = await reopened.GetUnfinishedAsync(
            TestContext.Current.CancellationToken);
        if (quarantineActive)
        {
            Assert.Empty(reopenedUnfinished);
        }
        else
        {
            Assert.Equal(active.Id, Assert.Single(reopenedUnfinished).Id);
        }

        Assert.Empty((await reopened.GetHistoryPageAsync(
            null,
            10,
            TestContext.Current.CancellationToken)).Items);
    }

    [Fact]
    public async Task LegacyHistoryQuarantineFollowsExplicitHistoryProjection()
    {
        var completed = _fixture.CreateCompletedTask(
            "legacy-history-quarantine",
            123,
            Path.Combine(_fixture.TempDirectory, "history-quarantine", "video"));
        await _fixture.CreateVersionThreeDatabaseAsync(completed);
        await _fixture.InsertLegacyQuarantineAsync("downloaded", completed.Id.Value);
        using var store = _fixture.CreateStore();

        await store.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Empty((await store.GetHistoryPageAsync(
            null,
            10,
            TestContext.Current.CancellationToken)).Items);
        var quarantine = Assert.Single(
            await store.GetQuarantinedRecordsAsync(TestContext.Current.CancellationToken));
        Assert.Equal("download_history", quarantine.SourceTable);
        Assert.Equal(completed.Id.Value, quarantine.RecordId);
        Assert.Equal("legacy-downloaded-corrupt-record", quarantine.Reason);
        Assert.Equal(0, await _fixture.CountDownloadBaseRecordAsync(completed.Id.Value));
        Assert.False(await _fixture.TableExistsAsync("downloaded"));
    }

    [Fact]
    public async Task VersionFourMigrationAndRestartAreIdempotentAcrossMultipleLegacyTasks()
    {
        var safePath = Path.Combine(_fixture.TempDirectory, "safe", "video");
        await _fixture.CreateVersionThreeDatabaseAsync(
            _fixture.CreatePausedTask("unsafe-a", Path.Combine(_fixture.TempDirectory, "logical-a", "video")),
            _fixture.CreatePausedTask("safe", safePath),
            _fixture.CreatePausedTask("unsafe-b", Path.Combine(_fixture.TempDirectory, "logical-b", "video")));
        using (var first = _fixture.CreateStore(new SqliteDownloadStoreFixture.StubPhysicalOutputPathResolver(path =>
               path == safePath ? path : path + "-physical")))
        {
            await first.InitializeAsync(TestContext.Current.CancellationToken);
            await first.InitializeAsync(TestContext.Current.CancellationToken);
            Assert.Equal(
                ["unsafe-a", "unsafe-b"],
                (await first.GetQuarantinedRecordsAsync(TestContext.Current.CancellationToken))
                    .Select(record => record.RecordId));
            Assert.True(await first.IsLegacyUpgradeAdmissionBlockedAsync(
                TestContext.Current.CancellationToken));
        }

        using var reopened = _fixture.CreateStore(new SqliteDownloadStoreFixture.StubPhysicalOutputPathResolver(
            _ => throw new InvalidOperationException("Version four must not rescan.")));
        await reopened.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            ["unsafe-a", "unsafe-b"],
            (await reopened.GetQuarantinedRecordsAsync(TestContext.Current.CancellationToken))
                .Select(record => record.RecordId));
        Assert.Equal(
            "safe",
            Assert.Single(await reopened.GetUnfinishedAsync(TestContext.Current.CancellationToken)).Id.Value);
        Assert.True(await reopened.IsLegacyUpgradeAdmissionBlockedAsync(
            TestContext.Current.CancellationToken));
        Assert.Equal(1, await _fixture.CountSchemaMigrationAsync(DownloadStoreSchema.CurrentVersion));
    }

    [Fact]
    public async Task ConfirmationOnlyReleasesAdmissionGateAndPersistsAcrossRestart()
    {
        var originalPath = Path.Combine(_fixture.TempDirectory, "confirm-logical", "video");
        await _fixture.CreateVersionThreeDatabaseAsync(_fixture.CreatePausedTask("confirm", originalPath));
        var before = await _fixture.ReadStoredStateAsync("confirm");
        using (var store = _fixture.CreateStore(new SqliteDownloadStoreFixture.StubPhysicalOutputPathResolver(path => path + "-physical")))
        {
            await store.InitializeAsync(TestContext.Current.CancellationToken);
            Assert.True((await store.ConfirmLegacyRemoteTasksStoppedAsync(
                TestContext.Current.CancellationToken)).IsSuccess);
            Assert.False(await store.IsLegacyUpgradeAdmissionBlockedAsync(
                TestContext.Current.CancellationToken));
            Assert.Empty(await store.GetUnfinishedAsync(TestContext.Current.CancellationToken));
            Assert.Single(await store.GetQuarantinedRecordsAsync(TestContext.Current.CancellationToken));
            Assert.Equal(before, await _fixture.ReadStoredStateAsync("confirm"));
        }

        using var reopened = _fixture.CreateStore(new SqliteDownloadStoreFixture.StubPhysicalOutputPathResolver(
            _ => throw new InvalidOperationException("Version four must not rescan.")));
        Assert.False(await reopened.IsLegacyUpgradeAdmissionBlockedAsync(
            TestContext.Current.CancellationToken));
        Assert.Empty(await reopened.GetUnfinishedAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            "confirm",
            Assert.Single(
                await reopened.GetQuarantinedRecordsAsync(TestContext.Current.CancellationToken)).RecordId);
        Assert.Equal(before, await _fixture.ReadStoredStateAsync("confirm"));
    }

    [Fact]
    public async Task VersionFourFailureRollsBackQuarantineGateLedgerAndUserVersion()
    {
        var originalPath = Path.Combine(_fixture.TempDirectory, "rollback-logical", "video");
        await _fixture.CreateVersionThreeDatabaseAsync(_fixture.CreatePausedTask("rollback", originalPath));
        await _fixture.CreateQuarantineFailureTriggerAsync();
        var before = await _fixture.ReadStoredStateAsync("rollback");
        using var store = _fixture.CreateStore(new SqliteDownloadStoreFixture.StubPhysicalOutputPathResolver(path => path + "-physical"));

        await Assert.ThrowsAsync<SqliteException>(() =>
            store.InitializeAsync(TestContext.Current.CancellationToken));

        Assert.Equal(3, await _fixture.ReadSchemaVersionAsync());
        Assert.False(await _fixture.TableExistsAsync("download_upgrade_admission_gate"));
        Assert.Equal(0, await _fixture.CountSchemaMigrationAsync(4));
        Assert.Equal(0, await _fixture.CountQuarantineRecordsAsync());
        Assert.Equal(before, await _fixture.ReadStoredStateAsync("rollback"));
        Assert.Single(Directory.GetFiles(
            Path.Combine(_fixture.TempDirectory, "Backup"),
            "download.db.schema-v3-*.bak"));
    }

    [Fact]
    public async Task VersionSevenDatabaseUpgradesWithNoInventedPendingPublication()
    {
        var expected = _fixture.CreatePausedTask("v7-publishing-upgrade");
        using (var store = _fixture.CreateStore())
        {
            Assert.True((await store.AddAsync(expected, TestContext.Current.CancellationToken)).IsSuccess);
        }

        await _fixture.DowngradeCurrentDatabaseAsync(7).ConfigureAwait(true);
        using (var connection = await _fixture.OpenConnectionAsync(readOnly: true).ConfigureAwait(true))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('download_base') WHERE name = 'publishing_key'";
            Assert.Equal(0L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true));
        }

        _fixture.ClearStorePool(); // A process restart does not reuse a pre-migration SQLite connection.

        using var reopened = _fixture.CreateStore();
        await reopened.InitializeAsync(TestContext.Current.CancellationToken);
        var restored = Assert.Single(
            await reopened.GetUnfinishedAsync(TestContext.Current.CancellationToken));
        Assert.Equal(DownloadStoreSchema.CurrentVersion, await _fixture.ReadSchemaVersionAsync());
        Assert.Equal(1, await _fixture.CountSchemaMigrationAsync(DownloadStoreSchema.CurrentVersion));
        Assert.Equal(expected.Output.StagingToken, restored.Output.StagingToken);
        Assert.Null(restored.Output.PublishingArtifact);
    }

    [Fact]
    public async Task VersionFourRowMigratesWithNoInventedNfoIntent()
    {
        await _fixture.CreateVersionFourDatabaseAsync();
        using var store = _fixture.CreateStore();

        await store.InitializeAsync(TestContext.Current.CancellationToken);
        var restored = Assert.Single(
            await store.GetUnfinishedAsync(TestContext.Current.CancellationToken));

        Assert.Null(restored.Plan.NfoRequest);
        Assert.Equal(DownloadContentSelection.None, restored.Plan.RequestedContent);
        Assert.Null(restored.Plan.RequestedContent.MediaKind);
        Assert.Equal(DownloadStoreSchema.CurrentVersion, await _fixture.ReadSchemaVersionAsync());
        Assert.Equal(1, await _fixture.CountSchemaMigrationAsync(DownloadStoreSchema.CurrentVersion));
    }
}
