using DownKyi.Application.Downloads;
using DownKyi.Application.Time;
using DownKyi.Domain.Downloads;
using DownKyi.Infrastructure.Downloads;
using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Tests;

public sealed class SqliteDownloadTaskStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "downkyi-download-store-tests",
        Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 7, 13, 1, 2, 3, TimeSpan.Zero));

    [Fact]
    public async Task InitializeCreatesCurrentSchema()
    {
        using var store = CreateStore();

        await store.InitializeAsync(TestContext.Current.CancellationToken);

        using var connection = await OpenReadOnlyConnectionAsync().ConfigureAwait(true);
        using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        Assert.Equal(8L, await version.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        Assert.True(await TableExistsAsync("download_upgrade_admission_gate"));
        Assert.Equal(1, await CountSchemaMigrationAsync(4));
        Assert.Equal(1, await CountSchemaMigrationAsync(5));
        Assert.False(await store.IsLegacyUpgradeAdmissionBlockedAsync(
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InitializeBacksUpAndMigratesLegacyDatabaseInPlace()
    {
        await CreateLegacyDatabaseAsync();
        using var store = CreateStore();

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
            Path.Combine(_directory, "Backup"),
            "download.db.schema-v0-*.bak"));
    }

    [Fact]
    public async Task VersionThreeEquivalentPhysicalPathRemainsAvailableWithoutBlockingAdmission()
    {
        var originalPath = Path.Combine(_directory, "equivalent", "video");
        var equivalentPath = Path.Combine(originalPath, ".") + Path.DirectorySeparatorChar;
        if (Path.DirectorySeparatorChar != Path.AltDirectorySeparatorChar)
        {
            equivalentPath = equivalentPath.Replace(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        }

        if (DownloadOutputPathKey.UsesCaseInsensitiveComparison)
        {
            equivalentPath = equivalentPath.ToUpperInvariant();
        }

        await CreateVersionThreeDatabaseAsync(CreatePausedTask("equivalent", originalPath));
        var resolutionCalls = 0;
        using var store = CreateStore(new StubPhysicalOutputPathResolver(_ =>
        {
            resolutionCalls++;
            return equivalentPath;
        }));

        await store.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, resolutionCalls);
        Assert.Equal(
            "equivalent",
            Assert.Single(await store.GetUnfinishedAsync(TestContext.Current.CancellationToken)).Id.Value);
        Assert.Empty(await store.GetQuarantinedRecordsAsync(TestContext.Current.CancellationToken));
        Assert.False(await store.IsLegacyUpgradeAdmissionBlockedAsync(
            TestContext.Current.CancellationToken));
        Assert.Equal(originalPath, (await ReadStoredStateAsync("equivalent")).Path);
    }

    [Fact]
    public async Task VersionThreePhysicalPathChangeQuarantinesWithoutMutatingTaskOrFiles()
    {
        var originalPath = Path.Combine(_directory, "logical", "video");
        var physicalPath = Path.Combine(_directory, "physical", "video");
        var partialPath = Path.Combine(_directory, "logical", "video.download");
        var ariaPath = Path.Combine(_directory, "logical", "video.aria2");
        Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);
        await File.WriteAllTextAsync(partialPath, "partial", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(ariaPath, "aria", TestContext.Current.CancellationToken);
        await CreateVersionThreeDatabaseAsync(CreatePausedTask("changed", originalPath));
        var before = await ReadStoredStateAsync("changed");
        using var store = CreateStore(new StubPhysicalOutputPathResolver(_ => physicalPath));

        await store.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await store.GetUnfinishedAsync(TestContext.Current.CancellationToken));
        var quarantine = Assert.Single(
            await store.GetQuarantinedRecordsAsync(TestContext.Current.CancellationToken));
        Assert.Equal("changed", quarantine.RecordId);
        Assert.Equal("downloading", quarantine.SourceTable);
        Assert.Equal("file_path", quarantine.FieldName);
        Assert.Equal("legacy-output-path-unverified", quarantine.Reason);
        Assert.True(await store.IsLegacyUpgradeAdmissionBlockedAsync(
            TestContext.Current.CancellationToken));
        Assert.Equal(before, await ReadStoredStateAsync("changed"));
        Assert.True(File.Exists(partialPath));
        Assert.True(File.Exists(ariaPath));
    }

    [Fact]
    public async Task VersionThreePhysicalAliasCollisionQuarantinesEntireGroupOnly()
    {
        var physicalPath = Path.Combine(_directory, "physical", "video");
        var aliasPath = Path.Combine(_directory, "alias", "video");
        var otherPath = Path.Combine(_directory, "other", "video");
        await CreateVersionThreeDatabaseAsync(
            CreatePausedTask("physical", physicalPath),
            CreatePausedTask("alias", aliasPath),
            CreatePausedTask("other", otherPath));
        var physicalBefore = await ReadStoredStateAsync("physical");
        var aliasBefore = await ReadStoredStateAsync("alias");
        var otherBefore = await ReadStoredStateAsync("other");
        var resolutionCalls = new Dictionary<string, int>(StringComparer.Ordinal);
        using var store = CreateStore(new StubPhysicalOutputPathResolver(path =>
        {
            resolutionCalls[path] = resolutionCalls.GetValueOrDefault(path) + 1;
            return path == aliasPath ? physicalPath : path;
        }));

        await store.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, resolutionCalls.Count);
        Assert.All(resolutionCalls.Values, count => Assert.Equal(1, count));
        Assert.Equal(
            "other",
            Assert.Single(await store.GetUnfinishedAsync(TestContext.Current.CancellationToken)).Id.Value);
        Assert.Equal(
            ["alias", "physical"],
            (await store.GetQuarantinedRecordsAsync(TestContext.Current.CancellationToken))
                .Select(record => record.RecordId));
        Assert.True(await store.IsLegacyUpgradeAdmissionBlockedAsync(
            TestContext.Current.CancellationToken));
        Assert.Equal(physicalBefore, await ReadStoredStateAsync("physical"));
        Assert.Equal(aliasBefore, await ReadStoredStateAsync("alias"));
        Assert.Equal(otherBefore, await ReadStoredStateAsync("other"));
    }

    [Fact]
    public async Task VersionThreePhysicalResolverFailureQuarantinesAndBlocksAdmission()
    {
        var originalPath = Path.Combine(_directory, "unresolved", "video");
        var safePath = Path.Combine(_directory, "safe", "video");
        await CreateVersionThreeDatabaseAsync(
            CreatePausedTask("unresolved", originalPath),
            CreatePausedTask("safe", safePath));
        var before = await ReadStoredStateAsync("unresolved");
        using var store = CreateStore(new StubPhysicalOutputPathResolver(
            path => path == originalPath
                ? throw new IOException("Synthetic resolver failure.")
                : path));

        await store.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            "safe",
            Assert.Single(await store.GetUnfinishedAsync(TestContext.Current.CancellationToken)).Id.Value);
        Assert.Equal(
            "unresolved",
            Assert.Single(
                await store.GetQuarantinedRecordsAsync(TestContext.Current.CancellationToken)).RecordId);
        Assert.True(await store.IsLegacyUpgradeAdmissionBlockedAsync(
            TestContext.Current.CancellationToken));
        Assert.Equal(before, await ReadStoredStateAsync("unresolved"));
    }

    [Fact]
    public async Task VersionThreeCompletedTaskIsSkippedAndExistingQuarantineStillBlocksPathRisk()
    {
        var completed = CreateCompletedTask(
            "completed",
            123,
            Path.Combine(_directory, "completed", "video"));
        var quarantined = CreatePausedTask(
            "already-quarantined",
            Path.Combine(_directory, "quarantined", "video"));
        await CreateVersionThreeDatabaseAsync(completed, quarantined);
        await InsertPreexistingQuarantineAsync("already-quarantined");
        var completedBefore = await ReadStoredStateAsync("completed");
        var quarantinedBefore = await ReadStoredStateAsync("already-quarantined");
        var resolvedPaths = new List<string>();
        using var store = CreateStore(new StubPhysicalOutputPathResolver(path =>
        {
            resolvedPaths.Add(path);
            return path + "-physical";
        }));

        await store.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal([quarantined.Output.BasePath], resolvedPaths);
        Assert.Equal(
            "completed",
            Assert.Single((await store.GetHistoryPageAsync(
                null,
                10,
                TestContext.Current.CancellationToken)).Items).Id.Value);
        Assert.Empty(await store.GetUnfinishedAsync(TestContext.Current.CancellationToken));
        var quarantine = Assert.Single(
            await store.GetQuarantinedRecordsAsync(TestContext.Current.CancellationToken));
        Assert.Equal("already-quarantined", quarantine.RecordId);
        Assert.Equal("preexisting-corrupt-record", quarantine.Reason);
        Assert.True(await store.IsLegacyUpgradeAdmissionBlockedAsync(
            TestContext.Current.CancellationToken));
        Assert.Equal(completedBefore, await ReadStoredStateAsync("completed"));
        Assert.Equal(quarantinedBefore, await ReadStoredStateAsync("already-quarantined"));
    }

    [Fact]
    public async Task VersionFourMigrationAndRestartAreIdempotentAcrossMultipleLegacyTasks()
    {
        var safePath = Path.Combine(_directory, "safe", "video");
        await CreateVersionThreeDatabaseAsync(
            CreatePausedTask("unsafe-a", Path.Combine(_directory, "logical-a", "video")),
            CreatePausedTask("safe", safePath),
            CreatePausedTask("unsafe-b", Path.Combine(_directory, "logical-b", "video")));
        using (var first = CreateStore(new StubPhysicalOutputPathResolver(path =>
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

        using var reopened = CreateStore(new StubPhysicalOutputPathResolver(
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
        Assert.Equal(1, await CountSchemaMigrationAsync(4));
        Assert.Equal(1, await CountSchemaMigrationAsync(5));
    }

    [Fact]
    public async Task ConfirmationOnlyReleasesAdmissionGateAndPersistsAcrossRestart()
    {
        var originalPath = Path.Combine(_directory, "confirm-logical", "video");
        await CreateVersionThreeDatabaseAsync(CreatePausedTask("confirm", originalPath));
        var before = await ReadStoredStateAsync("confirm");
        using (var store = CreateStore(new StubPhysicalOutputPathResolver(path => path + "-physical")))
        {
            await store.InitializeAsync(TestContext.Current.CancellationToken);
            Assert.True((await store.ConfirmLegacyRemoteTasksStoppedAsync(
                TestContext.Current.CancellationToken)).IsSuccess);
            Assert.False(await store.IsLegacyUpgradeAdmissionBlockedAsync(
                TestContext.Current.CancellationToken));
            Assert.Empty(await store.GetUnfinishedAsync(TestContext.Current.CancellationToken));
            Assert.Single(await store.GetQuarantinedRecordsAsync(TestContext.Current.CancellationToken));
            Assert.Equal(before, await ReadStoredStateAsync("confirm"));
        }

        using var reopened = CreateStore(new StubPhysicalOutputPathResolver(
            _ => throw new InvalidOperationException("Version four must not rescan.")));
        Assert.False(await reopened.IsLegacyUpgradeAdmissionBlockedAsync(
            TestContext.Current.CancellationToken));
        Assert.Empty(await reopened.GetUnfinishedAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            "confirm",
            Assert.Single(
                await reopened.GetQuarantinedRecordsAsync(TestContext.Current.CancellationToken)).RecordId);
        Assert.Equal(before, await ReadStoredStateAsync("confirm"));
    }

    [Fact]
    public async Task VersionFourFailureRollsBackQuarantineGateLedgerAndUserVersion()
    {
        var originalPath = Path.Combine(_directory, "rollback-logical", "video");
        await CreateVersionThreeDatabaseAsync(CreatePausedTask("rollback", originalPath));
        await CreateQuarantineFailureTriggerAsync();
        var before = await ReadStoredStateAsync("rollback");
        using var store = CreateStore(new StubPhysicalOutputPathResolver(path => path + "-physical"));

        await Assert.ThrowsAsync<SqliteException>(() =>
            store.InitializeAsync(TestContext.Current.CancellationToken));

        Assert.Equal(3, await ReadSchemaVersionAsync());
        Assert.False(await TableExistsAsync("download_upgrade_admission_gate"));
        Assert.Equal(0, await CountSchemaMigrationAsync(4));
        Assert.Equal(0, await CountQuarantineRecordsAsync());
        Assert.Equal(before, await ReadStoredStateAsync("rollback"));
        Assert.Single(Directory.GetFiles(
            Path.Combine(_directory, "Backup"),
            "download.db.schema-v3-*.bak"));
    }

    [Fact]
    public async Task LegacySucceedStatusStillInDownloadingTableIsQueuedForRecovery()
    {
        await CreateLegacyDatabaseAsync();
        await SetLegacyDownloadStatusAsync(5);
        using var store = CreateStore();

        var restored = Assert.Single(
            await store.GetUnfinishedAsync(TestContext.Current.CancellationToken));

        Assert.Equal(DownloadPhase.Queued, restored.Phase);
    }

    [Fact]
    public async Task OrphanedLegacyDownloadingRecordIsDeletedDuringInitialization()
    {
        await CreateLegacyDatabaseAsync();
        await InsertOrphanedLegacyDownloadingRecordAsync();
        using var store = CreateStore();

        var restored = await store.GetUnfinishedAsync(TestContext.Current.CancellationToken);

        Assert.Equal("legacy-resume", Assert.Single(restored).Id.Value);
        Assert.Equal(0, await CountDownloadingRecordAsync("orphaned-download"));
    }

    [Fact]
    public async Task ValidDownloadingRecordIsPreservedDuringOrphanCleanup()
    {
        using (var store = CreateStore())
        {
            Assert.True((await store.AddAsync(
                CreatePausedTask("valid-download"),
                TestContext.Current.CancellationToken)).IsSuccess);
        }

        await InsertOrphanedLegacyDownloadingRecordAsync();
        using var reopened = CreateStore();
        var restored = Assert.Single(
            await reopened.GetUnfinishedAsync(TestContext.Current.CancellationToken));

        Assert.Equal("valid-download", restored.Id.Value);
        Assert.Equal(1, await CountDownloadBaseRecordAsync("valid-download"));
        Assert.Equal(1, await CountDownloadingRecordAsync("valid-download"));
        Assert.Equal(0, await CountDownloadingRecordAsync("orphaned-download"));
    }

    [Fact]
    public async Task OrphanCleanupIsIdempotent()
    {
        await CreateLegacyDatabaseAsync();
        await InsertOrphanedLegacyDownloadingRecordAsync();
        using (var first = CreateStore())
        {
            await first.InitializeAsync(TestContext.Current.CancellationToken);
        }

        using (var second = CreateStore())
        {
            await second.InitializeAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(0, await CountDownloadingRecordAsync("orphaned-download"));
        Assert.Equal(1, await CountDownloadBaseRecordAsync("legacy-resume"));
        Assert.Equal(1, await CountDownloadingRecordAsync("legacy-resume"));
    }

    [Fact]
    public async Task CurrentSchemaDatabaseStillCleansOrphanedDownloadingRecords()
    {
        using (var store = CreateStore())
        {
            await store.InitializeAsync(TestContext.Current.CancellationToken);
        }

        await InsertOrphanedLegacyDownloadingRecordAsync();
        using (var reopened = CreateStore())
        {
            await reopened.InitializeAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(8, await ReadSchemaVersionAsync());
        Assert.Equal(0, await CountDownloadingRecordAsync("orphaned-download"));
    }

    [Fact]
    public async Task MigratedLegacyDatabaseCleansOrphanedDownloadingRecords()
    {
        await CreateLegacyDatabaseAsync();
        await InsertOrphanedLegacyDownloadingRecordAsync();
        using var store = CreateStore();

        await store.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(8, await ReadSchemaVersionAsync());
        Assert.Equal(1, await CountDownloadBaseRecordAsync("legacy-resume"));
        Assert.Equal(1, await CountDownloadingRecordAsync("legacy-resume"));
        Assert.Equal(0, await CountDownloadingRecordAsync("orphaned-download"));
    }

    [Fact]
    public async Task OrphanCleanupDoesNotAffectDownloadHistory()
    {
        using (var store = CreateStore())
        {
            Assert.True((await store.AddAsync(
                CreateCompletedTask("history-preserved", 123),
                TestContext.Current.CancellationToken)).IsSuccess);
        }

        await InsertOrphanedLegacyDownloadingRecordAsync();
        using var reopened = CreateStore();
        var history = await reopened.GetHistoryPageAsync(null, 10, TestContext.Current.CancellationToken);

        Assert.Equal("history-preserved", Assert.Single(history.Items).Id.Value);
        Assert.Equal(1, await CountDownloadBaseRecordAsync("history-preserved"));
        Assert.Equal(1, await CountDownloadedRecordAsync("history-preserved"));
        Assert.Equal(0, await CountDownloadingRecordAsync("orphaned-download"));
    }

    [Fact]
    public async Task PausedTaskPreservesResumeStateAcrossReopen()
    {
        var expected = CreatePausedTask("resume-01");
        using (var store = CreateStore())
        {
            Assert.True((await store.AddAsync(expected, TestContext.Current.CancellationToken)).IsSuccess);
        }

        using var reopened = CreateStore();
        var restored = Assert.Single(
            await reopened.GetUnfinishedAsync(TestContext.Current.CancellationToken));

        Assert.Equal(expected.Id, restored.Id);
        Assert.Equal(expected.Version, restored.Version);
        Assert.Equal(expected.Phase, restored.Phase);
        Assert.Equal(expected.Transfer.BackendIdentity, restored.Transfer.BackendIdentity);
        Assert.Equal(expected.Transfer.CompletedFileKeys, restored.Transfer.CompletedFileKeys);
        Assert.Equal(expected.Output.StagingToken, restored.Output.StagingToken);
        Assert.Equal(expected.Plan.RequestedContent, restored.Plan.RequestedContent);
        Assert.Equal(expected.Plan.TransferFiles, restored.Plan.TransferFiles);
        Assert.Equal(expected.Progress, restored.Progress);
    }

    [Fact]
    public async Task CompletedPublishedArtifactMapSurvivesDatabaseReopen()
    {
        var media = Path.Combine(_directory, "published.flv");
        var subtitle = Path.Combine(_directory, "published.zh-Hant.srt");
        var task = DownloadTask.Create(
            new DownloadTaskId("published-reopen"),
            CreateMetadata("published-reopen"),
            CreatePlan(),
            new DownloadOutput(Path.Combine(_directory, "base-without-matching-suffix"), null),
            _clock.UtcNow);
        task = task.Start(_clock.UtcNow.AddSeconds(1)).RequireValue();
        var mediaPublishing = new DownloadPublishingArtifact(
            "media", Path.GetFileName(media), 3, new string('A', 64));
        task = task.BeginPublishingArtifact(mediaPublishing,
            _clock.UtcNow.AddSeconds(2)).RequireValue();
        task = task.RecordPublishedArtifact(mediaPublishing, media,
            _clock.UtcNow.AddSeconds(3)).RequireValue();
        var subtitlePublishing = new DownloadPublishingArtifact(
            "subtitle:zh-Hant", Path.GetFileName(subtitle), 3, new string('B', 64));
        task = task.BeginPublishingArtifact(subtitlePublishing,
            _clock.UtcNow.AddSeconds(4)).RequireValue();
        task = task.RecordPublishedArtifact(subtitlePublishing, subtitle,
            _clock.UtcNow.AddSeconds(5)).RequireValue();
        task = task.Complete(
            new DownloadCompletion(123, "finished", null),
            _clock.UtcNow.AddSeconds(6)).RequireValue();
        using (var store = CreateStore())
        {
            Assert.True((await store.AddAsync(task, TestContext.Current.CancellationToken)).IsSuccess);
        }

        using var reopened = CreateStore();
        var restored = Assert.Single((await reopened.GetHistoryPageAsync(
            null, 10, TestContext.Current.CancellationToken)).Items);

        Assert.Equal(DownloadPhase.Completed, restored.Phase);
        Assert.Equal(media, restored.Output.PublishedArtifacts["media"]);
        Assert.Equal(subtitle, restored.Output.PublishedArtifacts["subtitle:zh-Hant"]);
        Assert.Equal(task.Output.BasePath, restored.Output.BasePath);
        Assert.Equal(task.Output.StagingToken, restored.Output.StagingToken);
    }

    [Fact]
    public async Task PendingPublicationSurvivesDatabaseReopenWithoutBecomingPublished()
    {
        var task = DownloadTask.Create(
            new DownloadTaskId("publishing-reopen"),
            CreateMetadata("publishing-reopen"),
            CreatePlan(),
            new DownloadOutput(Path.Combine(_directory, "output"), null),
            _clock.UtcNow);
        task = task.Start(_clock.UtcNow.AddSeconds(1)).RequireValue();
        var publishing = new DownloadPublishingArtifact(
            "media", "output.mp4", 3, new string('A', 64));
        task = task.BeginPublishingArtifact(publishing, _clock.UtcNow.AddSeconds(2))
            .RequireValue();
        Assert.False(task.Complete(new DownloadCompletion(123, "finished", null),
            _clock.UtcNow.AddSeconds(3)).IsSuccess);
        using (var store = CreateStore())
        {
            Assert.True((await store.AddAsync(task, TestContext.Current.CancellationToken)).IsSuccess);
        }

        using var reopened = CreateStore();
        var restored = Assert.Single(
            await reopened.GetUnfinishedAsync(TestContext.Current.CancellationToken));
        Assert.Equal(publishing, restored.Output.PublishingArtifact);
        Assert.Empty(restored.Output.PublishedArtifacts);
        var canceled = restored.Cancel(restored.UpdatedAtUtc.AddSeconds(1)).RequireValue();
        var publishedAfterCancel = canceled.RecordPublishedArtifact(publishing,
            Path.Combine(_directory, publishing.FileName),
            canceled.UpdatedAtUtc.AddSeconds(1)).RequireValue();
        Assert.Equal(DownloadPhase.Canceled, publishedAfterCancel.Phase);
        Assert.Null(publishedAfterCancel.Output.PublishingArtifact);
    }

    [Fact]
    public async Task VersionSevenDatabaseUpgradesWithNoInventedPendingPublication()
    {
        var expected = CreatePausedTask("v7-publishing-upgrade");
        using (var store = CreateStore())
        {
            Assert.True((await store.AddAsync(expected, TestContext.Current.CancellationToken)).IsSuccess);
        }

        using (var connection = await OpenConnectionAsync(readOnly: false).ConfigureAwait(true))
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                ALTER TABLE download_base DROP COLUMN publishing_key;
                ALTER TABLE download_base DROP COLUMN publishing_file_name;
                ALTER TABLE download_base DROP COLUMN publishing_length;
                ALTER TABLE download_base DROP COLUMN publishing_sha256;
                DELETE FROM download_schema_migrations WHERE version = 8;
                PRAGMA user_version = 7;
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('download_base') WHERE name = 'publishing_key'";
            Assert.Equal(0L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true));
        }

        ClearStorePool(); // A process restart does not reuse a pre-migration SQLite connection.

        using var reopened = CreateStore();
        await reopened.InitializeAsync(TestContext.Current.CancellationToken);
        var restored = Assert.Single(
            await reopened.GetUnfinishedAsync(TestContext.Current.CancellationToken));
        Assert.Equal(8, await ReadSchemaVersionAsync());
        Assert.Equal(1, await CountSchemaMigrationAsync(8));
        Assert.Equal(expected.Output.StagingToken, restored.Output.StagingToken);
        Assert.Null(restored.Output.PublishingArtifact);
    }

    [Fact]
    public async Task VersionFourRowMigratesWithNoInventedNfoIntent()
    {
        await CreateVersionFourDatabaseAsync();
        using var store = CreateStore();

        await store.InitializeAsync(TestContext.Current.CancellationToken);
        var restored = Assert.Single(
            await store.GetUnfinishedAsync(TestContext.Current.CancellationToken));

        Assert.Null(restored.Plan.NfoRequest);
        Assert.Equal(DownloadContentSelection.None, restored.Plan.RequestedContent);
        Assert.Equal(8, await ReadSchemaVersionAsync());
        Assert.Equal(1, await CountSchemaMigrationAsync(5));
    }

    [Fact]
    public async Task TypedNfoRequestRoundTripsAcrossReopen()
    {
        var expected = CreatePausedTask("nfo-resume", nfoRequest: CreateNfoRequest());
        using (var store = CreateStore())
        {
            Assert.True((await store.AddAsync(expected, TestContext.Current.CancellationToken)).IsSuccess);
        }

        using var reopened = CreateStore();
        var restored = Assert.Single(
            await reopened.GetUnfinishedAsync(TestContext.Current.CancellationToken));

        var request = Assert.IsType<DownloadNfoRequest>(restored.Plan.NfoRequest);
        Assert.Equal("Saved title", request.Title);
        Assert.Equal("Saved plot", request.Plot);
        Assert.Equal("2026", request.Year);
        Assert.Equal(["genre", "genre"], request.Genres);
        Assert.Equal(["tag"], request.Tags);
        Assert.Equal([new DownloadNfoActor("actor", "role")], request.Actors);
        Assert.Equal(new DownloadNfoUniqueId("bilibili", "BV1NFO"), request.BilibiliId);
        Assert.Equal("2026-09-09", request.Premiered);
        Assert.Equal([new DownloadNfoRating("bilibili", 9.5f, 10, true)], request.Ratings);
    }

    [Fact]
    public async Task TypedRequestedContentRoundTripsAcrossReopenUsingLegacyWireKeys()
    {
        var expected = new DownloadContentSelection(
            Audio: true,
            Video: false,
            Danmaku: true,
            Subtitle: false,
            Cover: true);
        using (var store = CreateStore())
        {
            Assert.True((await store.AddAsync(
                CreatePausedTask("typed-content", requestedContent: expected),
                TestContext.Current.CancellationToken)).IsSuccess);
        }

        using var reopened = CreateStore();
        var restored = Assert.Single(
            await reopened.GetUnfinishedAsync(TestContext.Current.CancellationToken));

        Assert.Equal(expected, restored.Plan.RequestedContent);
        using var connection = await OpenReadOnlyConnectionAsync().ConfigureAwait(true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT need_download_content FROM download_base WHERE id = 'typed-content'";
        var json = Assert.IsType<string>(
            await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        using var payload = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(5, payload.RootElement.EnumerateObject().Count());
        Assert.True(payload.RootElement.GetProperty("downloadAudio").GetBoolean());
        Assert.False(payload.RootElement.GetProperty("downloadVideo").GetBoolean());
        Assert.True(payload.RootElement.GetProperty("downloadDanmaku").GetBoolean());
        Assert.False(payload.RootElement.GetProperty("downloadSubtitle").GetBoolean());
        Assert.True(payload.RootElement.GetProperty("downloadCover").GetBoolean());
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("""{"Version":1,"Title":"x","Plot":"x","Year":"2026","Genres":null,"Tags":[],"Actors":[],"BilibiliId":null,"Premiered":"","Ratings":[]}""")]
    public async Task CorruptModernNfoRequestIsQuarantined(string payload)
    {
        using var store = CreateStore();
        Assert.True((await store.AddAsync(
            CreatePausedTask("valid-nfo-sibling"),
            TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await store.AddAsync(
            CreatePausedTask("corrupt-nfo", nfoRequest: CreateNfoRequest()),
            TestContext.Current.CancellationToken)).IsSuccess);
        await ReplaceNfoRequestAsync("corrupt-nfo", payload);

        Assert.Equal(
            "valid-nfo-sibling",
            Assert.Single(await store.GetUnfinishedAsync(TestContext.Current.CancellationToken)).Id.Value);
        var quarantine = Assert.Single(
            await store.GetQuarantinedRecordsAsync(TestContext.Current.CancellationToken));
        Assert.Equal("corrupt-nfo", quarantine.RecordId);
        Assert.Equal("nfo_request", quarantine.FieldName);
        Assert.DoesNotContain(payload, quarantine.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateRejectsAStaleVersion()
    {
        var original = DownloadTask.Create(
            new DownloadTaskId("versioned"),
            CreateMetadata("Versioned"),
            CreatePlan(),
            new DownloadOutput("output", null),
            _clock.UtcNow);
        using var store = CreateStore();
        Assert.True((await store.AddAsync(original, TestContext.Current.CancellationToken)).IsSuccess);
        var started = original.Start(_clock.UtcNow.AddSeconds(1)).RequireValue();
        Assert.True((await store.UpdateAsync(started, original.Version, TestContext.Current.CancellationToken)).IsSuccess);
        var paused = started.Pause(_clock.UtcNow.AddSeconds(2)).RequireValue();

        var stale = await store.UpdateAsync(paused, original.Version, TestContext.Current.CancellationToken);

        Assert.False(stale.IsSuccess);
        Assert.Equal("download.store.conflict", stale.Error?.Code);
    }

    [Fact]
    public async Task ConcurrentAddsAtomicallyClaimOneOutputPath()
    {
        var outputPath = Path.Combine(_directory, "shared-output");
        var first = CreateQueuedTask("claim-first", outputPath);
        var second = CreateQueuedTask("claim-second", outputPath);
        using var firstStore = CreateStore();
        using var secondStore = CreateStore();

        var results = await Task.WhenAll(
            firstStore.AddAsync(first, TestContext.Current.CancellationToken),
            secondStore.AddAsync(second, TestContext.Current.CancellationToken));

        Assert.Single(results, result => result.IsSuccess);
        var rejected = Assert.Single(results, result => !result.IsSuccess);
        Assert.Equal("download.store.output_path_reserved", rejected.Error?.Code);
    }

    [Fact]
    public async Task CanceledOutputClaimIsReleasedOnlyWhenTaskIsDeleted()
    {
        var outputPath = Path.Combine(_directory, "cleanup-owned-output");
        var task = CreateQueuedTask("cleanup-owner", outputPath);
        using var store = CreateStore();
        Assert.True((await store.AddAsync(task, TestContext.Current.CancellationToken)).IsSuccess);
        var canceled = task.Cancel(_clock.UtcNow.AddSeconds(1)).RequireValue();
        Assert.True((await store
            .UpdateAsync(canceled, task.Version, TestContext.Current.CancellationToken)).IsSuccess);

        Assert.True(await store.IsOutputPathReservedAsync(
            outputPath,
            DownloadOutputPathKey.UsesCaseInsensitiveComparison,
            TestContext.Current.CancellationToken));
        Assert.Contains(
            DownloadOutputPathKey.Create(outputPath, DownloadOutputPathKey.UsesCaseInsensitiveComparison),
            await store.GetActiveOutputReservationKeysAsync(
                DownloadOutputPathKey.UsesCaseInsensitiveComparison,
                TestContext.Current.CancellationToken));

        var deleted = canceled.Delete(_clock.UtcNow.AddSeconds(2)).RequireValue();
        Assert.True((await store
            .UpdateAsync(deleted, canceled.Version, TestContext.Current.CancellationToken)).IsSuccess);
        Assert.False(await store.IsOutputPathReservedAsync(
            outputPath,
            DownloadOutputPathKey.UsesCaseInsensitiveComparison,
            TestContext.Current.CancellationToken));
        Assert.DoesNotContain(
            DownloadOutputPathKey.Create(outputPath, DownloadOutputPathKey.UsesCaseInsensitiveComparison),
            await store.GetActiveOutputReservationKeysAsync(
                DownloadOutputPathKey.UsesCaseInsensitiveComparison,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReservationSnapshotIncludesFailedAndPausedButExcludesCompletedAndQuarantined()
    {
        using var store = CreateStore();
        var failed = CreateQueuedTask("failed-snapshot", Path.Combine(_directory, "failed-output"));
        failed = failed.Start(_clock.UtcNow.AddSeconds(1)).RequireValue();
        failed = failed.Fail(
            new DownloadFailure("download.failed", "Transfer failed.", true),
            _clock.UtcNow.AddSeconds(2)).RequireValue();
        var paused = CreatePausedTask("paused-snapshot");
        var completed = CreateCompletedTask("completed-snapshot", 10);
        var quarantined = CreateQueuedTask("quarantined-snapshot", Path.Combine(_directory, "quarantined"));
        foreach (var task in new[] { failed, paused, completed, quarantined })
        {
            Assert.True((await store.AddAsync(task, TestContext.Current.CancellationToken)).IsSuccess);
        }

        await InsertPreexistingQuarantineAsync(quarantined.Id.Value);
        var keys = await store.GetActiveOutputReservationKeysAsync(
            DownloadOutputPathKey.UsesCaseInsensitiveComparison,
            TestContext.Current.CancellationToken);

        Assert.Contains(DownloadOutputPathKey.Create(
            failed.Output.BasePath, DownloadOutputPathKey.UsesCaseInsensitiveComparison), keys);
        Assert.Contains(DownloadOutputPathKey.Create(
            paused.Output.BasePath, DownloadOutputPathKey.UsesCaseInsensitiveComparison), keys);
        Assert.DoesNotContain(DownloadOutputPathKey.Create(
            completed.Output.BasePath, DownloadOutputPathKey.UsesCaseInsensitiveComparison), keys);
        Assert.DoesNotContain(DownloadOutputPathKey.Create(
            quarantined.Output.BasePath, DownloadOutputPathKey.UsesCaseInsensitiveComparison), keys);
    }

    [Fact]
    public async Task ReservationSnapshotNormalizesLegacyNullKeyAndCaseVariants()
    {
        var basePath = Path.Combine(_directory, "cafe\u0301-output");
        using var store = CreateStore();
        Assert.True((await store.AddAsync(
            CreateQueuedTask("legacy-null-key", basePath),
            TestContext.Current.CancellationToken)).IsSuccess);
        using (var connection = await OpenConnectionAsync(readOnly: false))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE download_base SET output_reservation_key = NULL WHERE id = 'legacy-null-key'
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var caseSensitive = await store.GetActiveOutputReservationKeysAsync(
            ignoreCase: false, TestContext.Current.CancellationToken);
        var caseInsensitive = await store.GetActiveOutputReservationKeysAsync(
            ignoreCase: true, TestContext.Current.CancellationToken);

        Assert.Contains(DownloadOutputPathKey.Create(basePath, false), caseSensitive);
        Assert.Contains(DownloadOutputPathKey.Create(basePath, true), caseInsensitive);
        Assert.DoesNotContain(DownloadOutputPathKey.Create(
            basePath.ToUpperInvariant(), false), caseSensitive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReservationPointQueryAgreesWithSnapshotForLegacyNormalizedPath(bool ignoreCase)
    {
        var decomposed = Path.Combine(_directory, "cafe\u0301-legacy");
        var composed = Path.Combine(_directory, "caf\u00e9-legacy");
        using var store = CreateStore();
        Assert.True((await store.AddAsync(
            CreateQueuedTask("legacy-point-equivalence", decomposed),
            TestContext.Current.CancellationToken)).IsSuccess);
        using (var connection = await OpenConnectionAsync(readOnly: false))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE download_base SET output_reservation_key = NULL
                WHERE id = 'legacy-point-equivalence'
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var key = DownloadOutputPathKey.Create(composed, ignoreCase);
        var snapshot = await store.GetActiveOutputReservationKeysAsync(
            ignoreCase, TestContext.Current.CancellationToken);
        Assert.Contains(key, snapshot);
        Assert.True(await store.IsOutputPathReservedAsync(
            composed, ignoreCase, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReservationPointQueryMatchesSnapshotOnUnchangedMixedFixture()
    {
        var ignoreCase = DownloadOutputPathKey.UsesCaseInsensitiveComparison;
        var normal = Path.Combine(_directory, "normal-caf\u00e9");
        var legacy = Path.Combine(_directory, "legacy-cafe\u0301");
        var completed = Path.Combine(_directory, "completed-output");
        var quarantined = Path.Combine(_directory, "quarantined-output");
        using var store = CreateStore();
        Assert.True((await store.AddAsync(
            CreateQueuedTask("normal-equivalence", normal),
            TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await store.AddAsync(
            CreateQueuedTask("legacy-equivalence", legacy),
            TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await store.AddAsync(
            CreateCompletedTask("completed-equivalence", 10, completed),
            TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await store.AddAsync(
            CreateQueuedTask("quarantined-equivalence", quarantined),
            TestContext.Current.CancellationToken)).IsSuccess);
        using (var connection = await OpenConnectionAsync(readOnly: false))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE download_base SET output_reservation_key = NULL
                WHERE id = 'legacy-equivalence'
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        await InsertPreexistingQuarantineAsync("quarantined-equivalence");

        var snapshot = await store.GetActiveOutputReservationKeysAsync(
            ignoreCase, TestContext.Current.CancellationToken);
        foreach (var (candidate, expected) in new[]
        {
            (normal, true),
            (normal.ToUpperInvariant(), ignoreCase),
            (legacy.Normalize(System.Text.NormalizationForm.FormC), true),
            (legacy.ToUpperInvariant(), ignoreCase),
            (completed, false),
            (quarantined, false),
            (Path.Combine(_directory, "unrelated"), false)
        })
        {
            var key = DownloadOutputPathKey.Create(candidate, ignoreCase);
            var snapshotReserved = snapshot.Contains(key, StringComparer.Ordinal);
            var pointReserved = await store.IsOutputPathReservedAsync(
                candidate, ignoreCase, TestContext.Current.CancellationToken);
            Assert.Equal(expected, snapshotReserved);
            Assert.Equal(snapshotReserved, pointReserved);
        }
    }

    [Fact]
    public async Task ReopenedForeignPolicyReservationAgreesAcrossPointSnapshotAndAdd()
    {
        var ignoreCase = DownloadOutputPathKey.UsesCaseInsensitiveComparison;
        var original = Path.Combine(_directory, "cafe\u0301-foreign");
        var equivalent = Path.Combine(
            _directory,
            ignoreCase ? "CAF\u00c9-FOREIGN" : "caf\u00e9-foreign");
        using (var first = CreateStore())
        {
            Assert.True((await first.AddAsync(
                CreateQueuedTask("foreign-policy-original", original),
                TestContext.Current.CancellationToken)).IsSuccess);
        }

        using (var connection = await OpenConnectionAsync(readOnly: false))
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

        using var reopened = CreateStore();
        await reopened.InitializeAsync(TestContext.Current.CancellationToken);
        var key = DownloadOutputPathKey.Create(equivalent, ignoreCase);
        Assert.Contains(key, await reopened.GetActiveOutputReservationKeysAsync(
            ignoreCase, TestContext.Current.CancellationToken));
        Assert.True(await reopened.IsOutputPathReservedAsync(
            equivalent, ignoreCase, TestContext.Current.CancellationToken));
        Assert.False((await reopened.AddAsync(
            CreateQueuedTask("foreign-policy-second", equivalent),
            TestContext.Current.CancellationToken)).IsSuccess);
        var alternatePolicy = !ignoreCase;
        var alternateEquivalent = Path.Combine(_directory,
            ignoreCase ? "caf\u00e9-foreign" : "CAF\u00c9-FOREIGN");
        var alternateKey = DownloadOutputPathKey.Create(alternateEquivalent, alternatePolicy);
        Assert.Contains(alternateKey, await reopened.GetActiveOutputReservationKeysAsync(
            alternatePolicy, TestContext.Current.CancellationToken));
        Assert.True(await reopened.IsOutputPathReservedAsync(alternateEquivalent,
            alternatePolicy, TestContext.Current.CancellationToken));
        if (ignoreCase)
        {
            var distinctUnderAlternatePolicy = Path.Combine(_directory, "CAF\u00c9-FOREIGN");
            Assert.False(await reopened.IsOutputPathReservedAsync(distinctUnderAlternatePolicy,
                alternatePolicy, TestContext.Current.CancellationToken));
        }
        Assert.Equal(DownloadOutputPathKey.Create(original, ignoreCase),
            await ReadReservationKeyAsync("foreign-policy-original"));
        var backupPath = Assert.Single(Directory.GetFiles(Path.Combine(_directory, "Backup"),
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
        var original = Path.Combine(_directory, "foreign-cafe\u0301");
        var legacy = Path.Combine(_directory, "legacy");
        using (var first = CreateStore())
        {
            var pending = CreatePausedTask("rekey-original", original);
            pending = pending.UpdateOutput(new DownloadOutput(original, "1 GB",
                new Dictionary<string, string> { ["cover"] = "cover.jpg" },
                pending.Output.StagingToken,
                new DownloadPublishingArtifact("media", "video.mp4", 3,
                    new string('A', 64))), _clock.UtcNow.AddSeconds(6)).RequireValue();
            Assert.True((await first.AddAsync(pending,
                TestContext.Current.CancellationToken)).IsSuccess);
            Assert.True((await first.AddAsync(CreatePausedTask("rekey-legacy", legacy),
                TestContext.Current.CancellationToken)).IsSuccess);
            Assert.True((await first.AddAsync(CreateCompletedTask("rekey-history", 123),
                TestContext.Current.CancellationToken)).IsSuccess);
        }

        var originalBefore = await ReadStoredStateAsync("rekey-original");
        var legacyBefore = await ReadStoredStateAsync("rekey-legacy");
        var historyBefore = await ReadStoredStateAsync("rekey-history");
        var publicationBefore = await ReadPublicationPayloadAsync("rekey-original");
        await SetReservationKeyAsync("rekey-original",
            DownloadOutputPathKey.Create(original, !ignoreCase));
        await SetReservationKeyAsync("rekey-legacy", null);
        using (var reopened = CreateStore())
        {
            await reopened.InitializeAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(DownloadOutputPathKey.Create(original, ignoreCase),
            await ReadReservationKeyAsync("rekey-original"));
        Assert.Equal(DownloadOutputPathKey.Create(legacy, ignoreCase),
            await ReadReservationKeyAsync("rekey-legacy"));
        Assert.Null(await ReadReservationKeyAsync("rekey-history"));
        Assert.Equal(originalBefore, await ReadStoredStateAsync("rekey-original"));
        Assert.Equal(legacyBefore, await ReadStoredStateAsync("rekey-legacy"));
        Assert.Equal(historyBefore, await ReadStoredStateAsync("rekey-history"));
        Assert.Equal(publicationBefore, await ReadPublicationPayloadAsync("rekey-original"));
        using (var repeated = CreateStore())
        {
            await repeated.InitializeAsync(TestContext.Current.CancellationToken);
        }
        Assert.Single(Directory.GetFiles(Path.Combine(_directory, "Backup"),
            "download.db.reservation-keys-*.bak"));

        await SetReservationKeyAsync("rekey-original",
            DownloadOutputPathKey.Create(original, !ignoreCase));
        using (var switched = CreateStore())
        {
            await switched.InitializeAsync(TestContext.Current.CancellationToken);
        }
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_directory, "Backup"),
            "download.db.reservation-keys-*.bak").Length);
    }

    [Fact]
    public async Task CanonicalCollisionBlocksStoreAndDirectAddWithoutChangingAnyTask()
    {
        var composed = Path.Combine(_directory, "caf\u00e9-conflict");
        var decomposed = Path.Combine(_directory, "cafe\u0301-conflict");
        using (var first = CreateStore())
        {
            Assert.True((await first.AddAsync(CreatePausedTask("collision-a", composed),
                TestContext.Current.CancellationToken)).IsSuccess);
            Assert.True((await first.AddAsync(CreatePausedTask("collision-b"),
                TestContext.Current.CancellationToken)).IsSuccess);
        }

        await SetPathAndReservationKeyAsync("collision-b", decomposed,
            DownloadOutputPathKey.Create(decomposed,
                !DownloadOutputPathKey.UsesCaseInsensitiveComparison));
        var firstBefore = await ReadStoredStateAsync("collision-a");
        var secondBefore = await ReadStoredStateAsync("collision-b");
        var firstKey = await ReadReservationKeyAsync("collision-a");
        var secondKey = await ReadReservationKeyAsync("collision-b");
        using var reopened = CreateStore();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reopened.InitializeAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reopened.AddAsync(CreateQueuedTask("collision-third",
                Path.Combine(_directory, "unrelated")), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reopened.GetUnfinishedAsync(TestContext.Current.CancellationToken));
        Assert.Equal(firstBefore, await ReadStoredStateAsync("collision-a"));
        Assert.Equal(secondBefore, await ReadStoredStateAsync("collision-b"));
        Assert.Equal(firstKey, await ReadReservationKeyAsync("collision-a"));
        Assert.Equal(secondKey, await ReadReservationKeyAsync("collision-b"));
        Assert.False(Directory.Exists(Path.Combine(_directory, "Backup")));
    }

    [Fact]
    public async Task ReservationRekeyPreflightsQuarantineAndCompletedUniqueOccupants()
    {
        var target = Path.Combine(_directory, "reserved-target");
        using (var first = CreateStore())
        {
            Assert.True((await first.AddAsync(CreatePausedTask("occupied-quarantine", target),
                TestContext.Current.CancellationToken)).IsSuccess);
            Assert.True((await first.AddAsync(CreatePausedTask("moving", Path.Combine(_directory, "old")),
                TestContext.Current.CancellationToken)).IsSuccess);
        }
        await InsertPreexistingQuarantineAsync("occupied-quarantine");
        await SetPathAndReservationKeyAsync("moving", target,
            DownloadOutputPathKey.Create(Path.Combine(_directory, "old"),
                DownloadOutputPathKey.UsesCaseInsensitiveComparison));
        using (var reopened = CreateStore())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                reopened.InitializeAsync(TestContext.Current.CancellationToken));
        }

        // A completed row is not an active reservation, but a non-NULL key
        // still occupies the actual SQLite UNIQUE index.
        await SetPathAndReservationKeyAsync("moving", Path.Combine(_directory, "old"),
            DownloadOutputPathKey.Create(Path.Combine(_directory, "old"),
                DownloadOutputPathKey.UsesCaseInsensitiveComparison));
        var safeQuarantinePath = Path.Combine(_directory, "quarantine-safe");
        await SetPathAndReservationKeyAsync("occupied-quarantine", safeQuarantinePath,
            DownloadOutputPathKey.Create(safeQuarantinePath,
                DownloadOutputPathKey.UsesCaseInsensitiveComparison));
        using (var store = CreateStore())
        {
            Assert.True((await store.AddAsync(CreateCompletedTask("occupied-history", 123),
                TestContext.Current.CancellationToken)).IsSuccess);
        }
        await SetReservationKeyAsync("occupied-history",
            DownloadOutputPathKey.Create(target,
                DownloadOutputPathKey.UsesCaseInsensitiveComparison));
        await SetPathAndReservationKeyAsync("moving", target,
            DownloadOutputPathKey.Create(Path.Combine(_directory, "old"),
                DownloadOutputPathKey.UsesCaseInsensitiveComparison));
        using var completedCollision = CreateStore();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            completedCollision.InitializeAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task QuarantinedRowsRetainNullOrForeignKeysWithoutBlockingUnrelatedRecovery()
    {
        var foreignPath = OperatingSystem.IsWindows() ? "/foreign/quarantined" : @"C:\foreign\quarantined";
        var quarantinedPath = Path.Combine(_directory, "quarantined-foreign-key");
        using (var first = CreateStore())
        {
            Assert.True((await first.AddAsync(CreatePausedTask("quarantined-null"),
                TestContext.Current.CancellationToken)).IsSuccess);
            Assert.True((await first.AddAsync(CreatePausedTask("quarantined-foreign",
                quarantinedPath), TestContext.Current.CancellationToken)).IsSuccess);
            Assert.True((await first.AddAsync(CreatePausedTask("safe-unfinished"),
                TestContext.Current.CancellationToken)).IsSuccess);
        }
        await InsertPreexistingQuarantineAsync("quarantined-null");
        await InsertPreexistingQuarantineAsync("quarantined-foreign");
        await SetPathAndReservationKeyAsync("quarantined-null", foreignPath, null);
        var foreignKey = DownloadOutputPathKey.Create(quarantinedPath,
            !DownloadOutputPathKey.UsesCaseInsensitiveComparison);
        await SetReservationKeyAsync("quarantined-foreign", foreignKey);

        using var reopened = CreateStore();
        await reopened.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal("safe-unfinished", Assert.Single(await reopened.GetUnfinishedAsync(
            TestContext.Current.CancellationToken)).Id.Value);
        Assert.Equal(2, (await reopened.GetQuarantinedRecordsAsync(
            TestContext.Current.CancellationToken)).Count);
        Assert.Null(await ReadReservationKeyAsync("quarantined-null"));
        Assert.Equal(foreignKey, await ReadReservationKeyAsync("quarantined-foreign"));
        Assert.Equal(foreignPath, (await ReadStoredStateAsync("quarantined-null")).Path);
        Assert.True((await reopened.AddAsync(CreateQueuedTask("safe-new",
            Path.Combine(_directory, "safe-new")), TestContext.Current.CancellationToken)).IsSuccess);
        Assert.False(Directory.Exists(Path.Combine(_directory, "Backup")));
    }

    [Fact]
    public async Task ReservationRekeySqlFailureRollsBackAllKeysAndKeepsPrechangeBackup()
    {
        var firstPath = Path.Combine(_directory, "rollback-a");
        var secondPath = Path.Combine(_directory, "rollback-b");
        using (var first = CreateStore())
        {
            Assert.True((await first.AddAsync(CreatePausedTask("rollback-a", firstPath),
                TestContext.Current.CancellationToken)).IsSuccess);
            Assert.True((await first.AddAsync(CreatePausedTask("rollback-b", secondPath),
                TestContext.Current.CancellationToken)).IsSuccess);
        }
        var firstForeign = DownloadOutputPathKey.Create(firstPath,
            !DownloadOutputPathKey.UsesCaseInsensitiveComparison);
        var secondForeign = DownloadOutputPathKey.Create(secondPath,
            !DownloadOutputPathKey.UsesCaseInsensitiveComparison);
        await SetReservationKeyAsync("rollback-a", firstForeign);
        await SetReservationKeyAsync("rollback-b", secondForeign);
        using (var connection = await OpenConnectionAsync(readOnly: false))
        using (var trigger = connection.CreateCommand())
        {
            trigger.CommandText = """
                CREATE TRIGGER reject_second_rekey BEFORE UPDATE OF output_reservation_key
                ON download_base WHEN NEW.id = 'rollback-b' AND NEW.output_reservation_key IS NOT NULL
                BEGIN SELECT RAISE(ABORT, 'synthetic rekey failure'); END;
                """;
            await trigger.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        using var reopened = CreateStore();
        await Assert.ThrowsAsync<SqliteException>(() =>
            reopened.InitializeAsync(TestContext.Current.CancellationToken));
        Assert.Equal(firstForeign, await ReadReservationKeyAsync("rollback-a"));
        Assert.Equal(secondForeign, await ReadReservationKeyAsync("rollback-b"));
        Assert.Single(Directory.GetFiles(Path.Combine(_directory, "Backup"),
            "download.db.reservation-keys-*.bak"));
    }

    [Fact]
    public async Task ReservationRekeyHandlesKeySwapWithoutDroppingUniqueIndex()
    {
        var firstPath = Path.Combine(_directory, "swap-a");
        var secondPath = Path.Combine(_directory, "swap-b");
        using (var store = CreateStore())
        {
            Assert.True((await store.AddAsync(CreateQueuedTask("swap-a", firstPath),
                TestContext.Current.CancellationToken)).IsSuccess);
            Assert.True((await store.AddAsync(CreateQueuedTask("swap-b", secondPath),
                TestContext.Current.CancellationToken)).IsSuccess);
        }
        var firstKey = DownloadOutputPathKey.Create(firstPath,
            DownloadOutputPathKey.UsesCaseInsensitiveComparison);
        var secondKey = DownloadOutputPathKey.Create(secondPath,
            DownloadOutputPathKey.UsesCaseInsensitiveComparison);
        await SetReservationKeyAsync("swap-a", null);
        await SetReservationKeyAsync("swap-b", null);
        await SetReservationKeyAsync("swap-a", secondKey);
        await SetReservationKeyAsync("swap-b", firstKey);

        using var reopened = CreateStore();
        await reopened.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(firstKey, await ReadReservationKeyAsync("swap-a"));
        Assert.Equal(secondKey, await ReadReservationKeyAsync("swap-b"));
        using var connection = await OpenReadOnlyConnectionAsync();
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
        var path = Path.Combine(_directory, "backup-failure");
        using (var first = CreateStore())
        {
            Assert.True((await first.AddAsync(CreatePausedTask("backup-failure", path),
                TestContext.Current.CancellationToken)).IsSuccess);
        }
        var foreign = DownloadOutputPathKey.Create(path,
            !DownloadOutputPathKey.UsesCaseInsensitiveComparison);
        await SetReservationKeyAsync("backup-failure", foreign);

        using var cancellation = new CancellationTokenSource();
        using (var canceled = CreateStore(clock: new CancelOnReadClock(_clock.UtcNow, cancellation)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                canceled.InitializeAsync(cancellation.Token));
        }
        Assert.Equal(foreign, await ReadReservationKeyAsync("backup-failure"));

        var backupDirectory = Path.Combine(_directory, "Backup");
        Directory.Delete(backupDirectory);
        await File.WriteAllTextAsync(backupDirectory, "prevent backup directory creation",
            TestContext.Current.CancellationToken);
        using (var failedBackup = CreateStore())
        {
            await Assert.ThrowsAnyAsync<IOException>(() =>
                failedBackup.InitializeAsync(TestContext.Current.CancellationToken));
        }
        Assert.Equal(foreign, await ReadReservationKeyAsync("backup-failure"));
        File.Delete(backupDirectory);

        using var reopened = CreateStore();
        await reopened.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DownloadOutputPathKey.Create(path,
            DownloadOutputPathKey.UsesCaseInsensitiveComparison),
            await ReadReservationKeyAsync("backup-failure"));
    }

    [Fact]
    public async Task UninterpretableForeignPathBlocksBeforeChangingExistingTask()
    {
        var original = Path.Combine(_directory, "native-path");
        using (var first = CreateStore())
        {
            Assert.True((await first.AddAsync(CreatePausedTask("foreign-path", original),
                TestContext.Current.CancellationToken)).IsSuccess);
        }
        var foreignPath = OperatingSystem.IsWindows() ? "/foreign/path" : @"C:\foreign\path";
        var originalKey = await ReadReservationKeyAsync("foreign-path");
        await SetPathAndReservationKeyAsync("foreign-path", foreignPath, originalKey);
        using var reopened = CreateStore();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reopened.InitializeAsync(TestContext.Current.CancellationToken));
        Assert.Equal(foreignPath, (await ReadStoredStateAsync("foreign-path")).Path);
        Assert.Equal(originalKey, await ReadReservationKeyAsync("foreign-path"));
    }

    [Fact]
    public async Task NewUninterpretablePathIsRejectedBeforeItCanPoisonReopen()
    {
        var foreignPath = OperatingSystem.IsWindows() ? "/foreign/path" : @"C:\foreign\path";
        using var store = CreateStore();
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.AddAsync(CreateQueuedTask("new-foreign-path", foreignPath),
                TestContext.Current.CancellationToken));

        Assert.Equal(0, await CountDownloadBaseRecordAsync("new-foreign-path"));
        var existing = CreateQueuedTask("update-foreign-path",
            Path.Combine(_directory, "safe-existing"));
        Assert.True((await store.AddAsync(existing,
            TestContext.Current.CancellationToken)).IsSuccess);
        var changed = existing.UpdateOutput(new DownloadOutput(foreignPath, null),
            _clock.UtcNow.AddSeconds(1)).RequireValue();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.UpdateAsync(changed, existing.Version,
                TestContext.Current.CancellationToken));
        Assert.Equal(existing.Output.BasePath,
            (await ReadStoredStateAsync(existing.Id.Value)).Path);
    }

    [Fact]
    public async Task ForwardSlashUncPathFollowsCurrentPlatformPolicyAcrossReopen()
    {
        const string path = "//server/share/downkyi-output";
        using (var first = CreateStore())
        {
            if (OperatingSystem.IsWindows())
            {
                Assert.True((await first.AddAsync(CreateQueuedTask("slash-unc", path),
                    TestContext.Current.CancellationToken)).IsSuccess);
            }
            else
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    first.AddAsync(CreateQueuedTask("slash-unc", path),
                        TestContext.Current.CancellationToken));
                Assert.True((await first.AddAsync(CreatePausedTask("legacy-slash-unc",
                    Path.Combine(_directory, "safe-unc")),
                    TestContext.Current.CancellationToken)).IsSuccess);
            }
        }

        if (OperatingSystem.IsWindows())
        {
            using var reopened = CreateStore();
            await reopened.InitializeAsync(TestContext.Current.CancellationToken);
            Assert.Equal(DownloadOutputPathKey.Create(path,
                DownloadOutputPathKey.UsesCaseInsensitiveComparison),
                await ReadReservationKeyAsync("slash-unc"));
            Assert.True(await reopened.IsOutputPathReservedAsync(path,
                DownloadOutputPathKey.UsesCaseInsensitiveComparison,
                TestContext.Current.CancellationToken));
        }
        else
        {
            var originalKey = await ReadReservationKeyAsync("legacy-slash-unc");
            await SetPathAndReservationKeyAsync("legacy-slash-unc", path, originalKey);
            using var reopened = CreateStore();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                reopened.InitializeAsync(TestContext.Current.CancellationToken));
            Assert.Equal(originalKey, await ReadReservationKeyAsync("legacy-slash-unc"));
            Assert.Equal(0, await CountDownloadBaseRecordAsync("slash-unc"));
        }
    }

    [Fact]
    public async Task ActiveUpdateRecomputesKeyAndUniqueRejectsEquivalentNewAdmission()
    {
        var original = Path.Combine(_directory, "update-original");
        var next = Path.Combine(_directory, "update-cafe\u0301");
        var equivalent = Path.Combine(_directory, "update-caf\u00e9");
        using var store = CreateStore();
        var task = CreateQueuedTask("update-path", original);
        Assert.True((await store.AddAsync(task, TestContext.Current.CancellationToken)).IsSuccess);
        await SetReservationKeyAsync(task.Id.Value, null);
        var updated = task.UpdateOutput(new DownloadOutput(next, null),
            _clock.UtcNow.AddSeconds(1)).RequireValue();
        Assert.True((await store.UpdateAsync(updated, task.Version,
            TestContext.Current.CancellationToken)).IsSuccess);
        Assert.Equal(DownloadOutputPathKey.Create(next,
            DownloadOutputPathKey.UsesCaseInsensitiveComparison),
            await ReadReservationKeyAsync(task.Id.Value));
        Assert.False((await store.AddAsync(CreateQueuedTask("update-duplicate", equivalent),
            TestContext.Current.CancellationToken)).IsSuccess);
    }

    [Fact]
    public async Task CoalescedProgressWriteAdvancesVersionAndPayloadAtomically()
    {
        var original = DownloadTask.Create(
            new DownloadTaskId("progress"),
            CreateMetadata("Progress"),
            CreatePlan(),
            new DownloadOutput("output", null),
            _clock.UtcNow).Start(_clock.UtcNow.AddSeconds(1)).RequireValue();
        using var store = CreateStore();
        Assert.True((await store.AddAsync(original, TestContext.Current.CancellationToken)).IsSuccess);
        var progress = new DownloadProgress(75, 750, 1000, 5_000_000, "750 B", "40 Mbps");

        var result = await store.UpdateProgressAsync(
            new DownKyi.Application.Downloads.DownloadProgressWrite(
                original.Id,
                progress,
                original.Version,
                original.Version + 3,
                _clock.UtcNow.AddSeconds(4)),
            TestContext.Current.CancellationToken);
        var restored = await store.FindAsync(original.Id, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.NotNull(restored);
        Assert.Equal(original.Version + 3, restored.Version);
        Assert.Equal(progress, restored.Progress);
    }

    [Fact]
    public async Task InitializationHonorsCancellationBeforeOpeningDatabase()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        using var store = CreateStore();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.InitializeAsync(cancellation.Token));

        Assert.False(File.Exists(Path.Combine(_directory, "download.db")));
    }

    [Fact]
    public async Task FailedMigrationRollsBackSchemaChangesAndKeepsBackup()
    {
        await CreateIncompatibleLegacyDatabaseAsync();
        using var store = CreateStore();

        await Assert.ThrowsAsync<SqliteException>(() =>
            store.InitializeAsync(TestContext.Current.CancellationToken));

        using var connection = await OpenReadOnlyConnectionAsync().ConfigureAwait(true);
        using var columns = connection.CreateCommand();
        columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('download_base') WHERE name = 'version'";
        Assert.Equal(0L, await columns.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(
            Path.Combine(_directory, "Backup"),
            "download.db.schema-v0-*.bak"));
    }

    [Fact]
    public async Task CorruptRecordIsQuarantinedWithoutHidingValidRecordsOrPrivateData()
    {
        const string sensitiveValue = "C:\\Users\\private-user\\Downloads\\secret";
        using var store = CreateStore();
        Assert.True((await store.AddAsync(
            CreatePausedTask("valid"),
            TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await store.AddAsync(
            CreatePausedTask("corrupt"),
            TestContext.Current.CancellationToken)).IsSuccess);
        await CorruptRequestedAssetsAsync("corrupt", sensitiveValue);

        var tasks = await store.GetUnfinishedAsync(TestContext.Current.CancellationToken);
        var quarantine = Assert.Single(
            await store.GetQuarantinedRecordsAsync(TestContext.Current.CancellationToken));

        Assert.Equal("valid", Assert.Single(tasks).Id.Value);
        Assert.Equal("corrupt", quarantine.RecordId);
        Assert.Equal("need_download_content", quarantine.FieldName);
        Assert.DoesNotContain(sensitiveValue, quarantine.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HistoryUsesStableKeysetPagination()
    {
        using var store = CreateStore();
        foreach (var (id, timestamp) in new[]
        {
            ("history-a", 100L),
            ("history-b", 300L),
            ("history-c", 200L)
        })
        {
            Assert.True((await store.AddAsync(
                CreateCompletedTask(id, timestamp),
                TestContext.Current.CancellationToken)).IsSuccess);
        }

        var first = await store.GetHistoryPageAsync(null, 2, TestContext.Current.CancellationToken);
        var second = await store.GetHistoryPageAsync(
            first.NextCursor,
            2,
            TestContext.Current.CancellationToken);

        Assert.Equal(["history-b", "history-c"], first.Items.Select(task => task.Id.Value));
        Assert.Equal("history-a", Assert.Single(second.Items).Id.Value);
        Assert.Null(second.NextCursor);
    }

    [Fact]
    public async Task DisposeDoesNotClearProviderPoolOwnedBySiblingConnections()
    {
        var databasePath = Path.Combine(_directory, "download.db");
        var store = CreateStore();
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 5
        }.ToString();

        using (var sibling = new SqliteConnection(connectionString))
        {
            await sibling.OpenAsync(TestContext.Current.CancellationToken);
            await using var createProbe = sibling.CreateCommand();
            createProbe.CommandText = "CREATE TEMP TABLE pool_owner_probe(value INTEGER);";
            await createProbe.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        store.Dispose();

        using var observer = new SqliteConnection(connectionString);
        await observer.OpenAsync(TestContext.Current.CancellationToken);
        await using var findProbe = observer.CreateCommand();
        findProbe.CommandText =
            "SELECT COUNT(*) FROM sqlite_temp_master WHERE type = 'table' AND name = 'pool_owner_probe';";

        Assert.Equal(
            1L,
            (long)(await findProbe.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
    }

    [Fact]
    public async Task DisposedStoreReportsThePublicStoreAsObjectName()
    {
        var store = CreateStore();
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        store.Dispose();

        var operation = store.GetUnfinishedAsync(TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await operation.ConfigureAwait(true));

        Assert.Equal(typeof(SqliteDownloadTaskStore).FullName, exception.ObjectName);
    }

    public void Dispose()
    {
        ClearStorePool();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private void ClearStorePool()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "download.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 5
        }.ToString());
        SqliteConnection.ClearPool(connection);
    }

    private SqliteDownloadTaskStore CreateStore(
        IPhysicalOutputPathResolver? resolver = null,
        IClock? clock = null)
    {
        Directory.CreateDirectory(_directory);
        return new SqliteDownloadTaskStore(
            new SqliteDownloadTaskStoreOptions(Path.Combine(_directory, "download.db")),
            clock ?? _clock,
            resolver ?? new StubPhysicalOutputPathResolver(static path => path));
    }

    private DownloadTask CreatePausedTask(
        string id,
        string? outputPath = null,
        DownloadNfoRequest? nfoRequest = null,
        DownloadContentSelection? requestedContent = null)
    {
        var task = DownloadTask.Create(
            new DownloadTaskId(id),
            CreateMetadata(id),
            CreatePlan(nfoRequest, requestedContent),
            new DownloadOutput(outputPath ?? Path.Combine(_directory, id), "1 GB"),
            _clock.UtcNow);
        task = task.Start(_clock.UtcNow.AddSeconds(1)).RequireValue();
        task = task.UpdateTransferState(
            new DownloadTransferState("aria-gid", ["cover"], "video", "Paused", 4_000_000),
            _clock.UtcNow.AddSeconds(2)).RequireValue();
        task = task.UpdateProgress(
            new DownloadProgress(42.5, 425, 1000, 3_000_000, "425 B", "24 Mbps"),
            _clock.UtcNow.AddSeconds(3)).RequireValue();
        task = task.Pause(_clock.UtcNow.AddSeconds(4)).RequireValue();
        return task.ConfirmPaused(_clock.UtcNow.AddSeconds(5)).RequireValue();
    }

    private DownloadTask CreateQueuedTask(string id, string outputPath)
    {
        return DownloadTask.Create(
            new DownloadTaskId(id),
            CreateMetadata(id),
            CreatePlan(),
            new DownloadOutput(outputPath, null),
            _clock.UtcNow);
    }

    private DownloadTask CreateCompletedTask(
        string id,
        long finishedTimestamp,
        string? outputPath = null)
    {
        var task = DownloadTask.Create(
            new DownloadTaskId(id),
            CreateMetadata(id),
            CreatePlan(),
            new DownloadOutput(outputPath ?? Path.Combine(_directory, id), "1 GB"),
            _clock.UtcNow);
        task = task.Start(_clock.UtcNow.AddSeconds(1)).RequireValue();
        return task.Complete(
            new DownloadCompletion(finishedTimestamp, "finished", "24 Mbps"),
            _clock.UtcNow.AddSeconds(2)).RequireValue();
    }

    private static DownloadTaskMetadata CreateMetadata(string name)
    {
        return new DownloadTaskMetadata(
            new DownloadMediaIdentity("BV1TEST", 1, 2, 3, 1, 1),
            "Collection",
            name,
            "00:10",
            "AVC",
            new DownloadQuality(80, "1080P"),
            new DownloadQuality(30280, "192K"),
            "cover",
            "page-cover",
            0);
    }

    private static DownloadPlan CreatePlan(
        DownloadNfoRequest? nfoRequest = null,
        DownloadContentSelection? requestedContent = null)
    {
        return new DownloadPlan(
            requestedContent ?? new DownloadContentSelection(false, true, false, false, false),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["video"] = "video.m4s" },
            1,
            nfoRequest);
    }

    private static DownloadNfoRequest CreateNfoRequest()
    {
        return new DownloadNfoRequest(
            "Saved title",
            "Saved plot",
            "2026",
            ["genre", "genre"],
            ["tag"],
            [new DownloadNfoActor("actor", "role")],
            new DownloadNfoUniqueId("bilibili", "BV1NFO"),
            "2026-09-09",
            [new DownloadNfoRating("bilibili", 9.5f, 10, true)]);
    }

    private async Task<SqliteConnection> OpenReadOnlyConnectionAsync()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "download.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        return connection;
    }

    private async Task<long> CountDownloadBaseRecordAsync(string id)
    {
        using var connection = await OpenReadOnlyConnectionAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM download_base WHERE id = @id";
        command.Parameters.AddWithValue("@id", id);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken).ConfigureAwait(false))!;
    }

    private async Task<long> CountDownloadingRecordAsync(string id)
    {
        using var connection = await OpenReadOnlyConnectionAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM downloading WHERE id = @id";
        command.Parameters.AddWithValue("@id", id);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken).ConfigureAwait(false))!;
    }

    private async Task<long> CountDownloadedRecordAsync(string id)
    {
        using var connection = await OpenReadOnlyConnectionAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM downloaded WHERE id = @id";
        command.Parameters.AddWithValue("@id", id);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken).ConfigureAwait(false))!;
    }

    private async Task<long> ReadSchemaVersionAsync()
    {
        using var connection = await OpenReadOnlyConnectionAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken).ConfigureAwait(false))!;
    }

    private async Task CreateVersionThreeDatabaseAsync(params DownloadTask[] tasks)
    {
        using (var store = CreateStore())
        {
            await store.InitializeAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
            foreach (var task in tasks)
            {
                Assert.True((await store.AddAsync(
                    task,
                    TestContext.Current.CancellationToken).ConfigureAwait(false)).IsSuccess);
            }
        }

        using var connection = await OpenConnectionAsync(readOnly: false).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            ALTER TABLE download_base DROP COLUMN publishing_key;
            ALTER TABLE download_base DROP COLUMN publishing_file_name;
            ALTER TABLE download_base DROP COLUMN publishing_length;
            ALTER TABLE download_base DROP COLUMN publishing_sha256;
            ALTER TABLE download_base DROP COLUMN nfo_request;
            ALTER TABLE download_base DROP COLUMN published_artifacts;
            ALTER TABLE download_base DROP COLUMN staging_token;
            DROP TABLE download_upgrade_admission_gate;
            DELETE FROM download_schema_migrations WHERE version IN (4, 5, 6, 7, 8);
            PRAGMA user_version = 3;
            """;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    private async Task InsertPreexistingQuarantineAsync(string id)
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

    private async Task SetReservationKeyAsync(string id, string? key)
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

    private async Task SetPathAndReservationKeyAsync(string id, string path, string? key)
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

    private async Task<string?> ReadReservationKeyAsync(string id)
    {
        using var connection = await OpenReadOnlyConnectionAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT output_reservation_key FROM download_base WHERE id = @id";
        command.Parameters.AddWithValue("@id", id);
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(false) as string;
    }

    private async Task<(string StagingToken, string PublishingKey, string PublishedArtifacts,
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

    private async Task CreateQuarantineFailureTriggerAsync()
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

    private async Task<StoredDownloadState> ReadStoredStateAsync(string id)
    {
        using var connection = await OpenReadOnlyConnectionAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT db.file_path, dl.gid, dl.download_files, dl.downloaded_files,
                   d.finished_timestamp
            FROM download_base db
            LEFT JOIN downloading dl ON dl.id = db.id
            LEFT JOIN downloaded d ON d.id = db.id
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

    private async Task<bool> TableExistsAsync(string tableName)
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

    private async Task<long> CountSchemaMigrationAsync(int version)
    {
        using var connection = await OpenReadOnlyConnectionAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM download_schema_migrations WHERE version = @version";
        command.Parameters.AddWithValue("@version", version);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(false))!;
    }

    private async Task<long> CountQuarantineRecordsAsync()
    {
        using var connection = await OpenReadOnlyConnectionAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM download_quarantine";
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(false))!;
    }

    private async Task CorruptRequestedAssetsAsync(string id, string sensitiveValue)
    {
        using var connection = await OpenConnectionAsync(readOnly: false).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE download_base SET need_download_content = @value WHERE id = @id";
        command.Parameters.AddWithValue("@value", sensitiveValue);
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    private async Task CreateVersionFourDatabaseAsync()
    {
        Directory.CreateDirectory(_directory);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "download.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(false);
        await DownloadStoreSchemaV1Migration.ApplyAsync(
            connection, transaction, _clock.UtcNow, TestContext.Current.CancellationToken)
            .ConfigureAwait(false);
        await DownloadStoreSchemaV2Migration.ApplyAsync(
            connection, transaction, _clock.UtcNow, TestContext.Current.CancellationToken)
            .ConfigureAwait(false);
        await DownloadStoreSchemaV3Migration.ApplyAsync(
            connection, transaction, _clock.UtcNow, TestContext.Current.CancellationToken)
            .ConfigureAwait(false);
        await DownloadStoreSchemaV4Migration.ApplyAsync(
            connection,
            transaction,
            _clock.UtcNow,
            new StubPhysicalOutputPathResolver(static path => path),
            TestContext.Current.CancellationToken)
            .ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO download_base
                (id, need_download_content, bvid, avid, cid, main_title, name, resolution,
                 file_path, version, created_at_utc, updated_at_utc)
            VALUES
                ('version-four', '{}', 'BV1V4', 1, 2, 'Version Four', 'Resume',
                 '{"Name":"1080P","Id":80}', 'version-four-output', 0, 1, 1);
            INSERT INTO downloading
                (id, download_files, downloaded_files, play_stream_type, download_status,
                 progress, max_speed, phase, bytes_per_second)
            VALUES
                ('version-four', '{}', '[]', 0, 3, 0, 0, @paused, 0);
            PRAGMA user_version = 4;
            """;
        command.Parameters.AddWithValue("@paused", (int)DownloadPhase.Paused);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    private async Task ReplaceNfoRequestAsync(string id, string payload)
    {
        using var connection = await OpenConnectionAsync(readOnly: false).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE download_base SET nfo_request = @payload WHERE id = @id";
        command.Parameters.AddWithValue("@payload", payload);
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    private async Task SetLegacyDownloadStatusAsync(int status)
    {
        using var connection = await OpenConnectionAsync(readOnly: false).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE downloading SET download_status = @status";
        command.Parameters.AddWithValue("@status", status);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    private async Task InsertOrphanedLegacyDownloadingRecordAsync()
    {
        using var connection = await OpenConnectionAsync(readOnly: false).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys = OFF;
            INSERT INTO downloading
                (id, download_files, downloaded_files, play_stream_type, download_status,
                 progress, max_speed)
            VALUES
                ('orphaned-download', '{}', '[]', 0, 0, 0, 0)
            """;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(bool readOnly)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "download.db"),
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        return connection;
    }

    private async Task CreateLegacyDatabaseAsync()
    {
        Directory.CreateDirectory(_directory);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "download.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE download_base (
                id TEXT PRIMARY KEY, need_download_content TEXT NOT NULL DEFAULT '{}',
                bvid TEXT NOT NULL DEFAULT '', avid INTEGER NOT NULL DEFAULT 0,
                cid INTEGER NOT NULL DEFAULT 0, episode_id INTEGER NOT NULL DEFAULT 0,
                cover_url TEXT NOT NULL DEFAULT '', page_cover_url TEXT NOT NULL DEFAULT '',
                zone_id INTEGER NOT NULL DEFAULT 0, [order] INTEGER NOT NULL DEFAULT 0,
                main_title TEXT NOT NULL DEFAULT '', name TEXT NOT NULL DEFAULT '',
                duration TEXT NOT NULL DEFAULT '', video_codec_name TEXT NOT NULL DEFAULT '',
                resolution TEXT NOT NULL DEFAULT '{}', audio_codec TEXT,
                file_path TEXT NOT NULL DEFAULT '', file_size TEXT, page INTEGER NOT NULL DEFAULT 1
            );
            CREATE TABLE downloading (
                id TEXT PRIMARY KEY REFERENCES download_base(id) ON DELETE CASCADE, gid TEXT,
                download_files TEXT NOT NULL DEFAULT '{}', downloaded_files TEXT NOT NULL DEFAULT '[]',
                play_stream_type INTEGER NOT NULL DEFAULT 0, download_status INTEGER NOT NULL DEFAULT 0,
                download_content TEXT, download_status_title TEXT, progress REAL NOT NULL DEFAULT 0,
                downloading_file_size TEXT, max_speed INTEGER NOT NULL DEFAULT 0, speed_display TEXT
            );
            CREATE TABLE downloaded (
                id TEXT PRIMARY KEY REFERENCES download_base(id) ON DELETE CASCADE,
                max_speed_display TEXT, finished_timestamp INTEGER NOT NULL DEFAULT 0,
                finished_time TEXT NOT NULL DEFAULT ''
            );
            INSERT INTO download_base
                (id, need_download_content, bvid, avid, cid, main_title, name, resolution,
                 audio_codec, file_path)
            VALUES
                ('legacy-resume', '{"video":true}', 'BV1LEGACY', 1, 2, 'Legacy', 'Resume',
                 '{"Name":"1080P","Id":80}', '{"Name":"192K","Id":30280}', 'legacy-output');
            INSERT INTO downloading
                (id, gid, download_files, downloaded_files, play_stream_type, download_status,
                 progress, max_speed)
            VALUES
                ('legacy-resume', 'aria-gid', '{"video":"video.m4s"}', '["cover"]', 1, 3, 42.5, 4000000);
            """;
        await schema.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    private async Task CreateIncompatibleLegacyDatabaseAsync()
    {
        Directory.CreateDirectory(_directory);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "download.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE download_base (
                id TEXT PRIMARY KEY, need_download_content TEXT NOT NULL DEFAULT '{}',
                bvid TEXT NOT NULL DEFAULT '', avid INTEGER NOT NULL DEFAULT 0,
                cid INTEGER NOT NULL DEFAULT 0, episode_id INTEGER NOT NULL DEFAULT 0,
                cover_url TEXT NOT NULL DEFAULT '', page_cover_url TEXT NOT NULL DEFAULT '',
                zone_id INTEGER NOT NULL DEFAULT 0, [order] INTEGER NOT NULL DEFAULT 0,
                main_title TEXT NOT NULL DEFAULT '', name TEXT NOT NULL DEFAULT '',
                duration TEXT NOT NULL DEFAULT '', video_codec_name TEXT NOT NULL DEFAULT '',
                resolution TEXT NOT NULL DEFAULT '{}', audio_codec TEXT,
                file_path TEXT NOT NULL DEFAULT '', file_size TEXT, page INTEGER NOT NULL DEFAULT 1
            );
            CREATE TABLE downloading (id TEXT PRIMARY KEY);
            """;
        await schema.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    private sealed class TestClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            return Task.Delay(delay, cancellationToken);
        }
    }

    private sealed class CancelOnReadClock(
        DateTimeOffset utcNow,
        CancellationTokenSource cancellation) : IClock
    {
        public DateTimeOffset UtcNow
        {
            get
            {
                cancellation.Cancel();
                return utcNow;
            }
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
            Task.Delay(delay, cancellationToken);
    }

    private sealed class StubPhysicalOutputPathResolver(Func<string, string> resolve)
        : IPhysicalOutputPathResolver
    {
        public string ResolvePhysicalBasePath(string logicalBasePath) => resolve(logicalBasePath);
    }

    private sealed record StoredDownloadState(
        string Path,
        string? Gid,
        string? DownloadFiles,
        string? DownloadedFiles,
        long? FinishedTimestamp);
}
