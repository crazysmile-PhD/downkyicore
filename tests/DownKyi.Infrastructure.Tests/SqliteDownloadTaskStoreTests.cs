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
        Assert.Equal(7L, await version.ExecuteScalarAsync(TestContext.Current.CancellationToken));
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

        Assert.Equal(7, await ReadSchemaVersionAsync());
        Assert.Equal(0, await CountDownloadingRecordAsync("orphaned-download"));
    }

    [Fact]
    public async Task MigratedLegacyDatabaseCleansOrphanedDownloadingRecords()
    {
        await CreateLegacyDatabaseAsync();
        await InsertOrphanedLegacyDownloadingRecordAsync();
        using var store = CreateStore();

        await store.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(7, await ReadSchemaVersionAsync());
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
        task = task.RecordPublishedArtifact("media", media, _clock.UtcNow.AddSeconds(2)).RequireValue();
        task = task.RecordPublishedArtifact("subtitle:zh-Hant", subtitle, _clock.UtcNow.AddSeconds(3)).RequireValue();
        task = task.Complete(
            new DownloadCompletion(123, "finished", null),
            _clock.UtcNow.AddSeconds(4)).RequireValue();
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
    public async Task VersionFourRowMigratesWithNoInventedNfoIntent()
    {
        await CreateVersionFourDatabaseAsync();
        using var store = CreateStore();

        await store.InitializeAsync(TestContext.Current.CancellationToken);
        var restored = Assert.Single(
            await store.GetUnfinishedAsync(TestContext.Current.CancellationToken));

        Assert.Null(restored.Plan.NfoRequest);
        Assert.Equal(DownloadContentSelection.None, restored.Plan.RequestedContent);
        Assert.Equal(7, await ReadSchemaVersionAsync());
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

        var deleted = canceled.Delete(_clock.UtcNow.AddSeconds(2)).RequireValue();
        Assert.True((await store
            .UpdateAsync(deleted, canceled.Version, TestContext.Current.CancellationToken)).IsSuccess);
        Assert.False(await store.IsOutputPathReservedAsync(
            outputPath,
            DownloadOutputPathKey.UsesCaseInsensitiveComparison,
            TestContext.Current.CancellationToken));
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
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "download.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 5
        }.ToString());
        SqliteConnection.ClearPool(connection);
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private SqliteDownloadTaskStore CreateStore(IPhysicalOutputPathResolver? resolver = null)
    {
        Directory.CreateDirectory(_directory);
        return new SqliteDownloadTaskStore(
            new SqliteDownloadTaskStoreOptions(Path.Combine(_directory, "download.db")),
            _clock,
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
            ALTER TABLE download_base DROP COLUMN nfo_request;
            ALTER TABLE download_base DROP COLUMN published_artifacts;
            ALTER TABLE download_base DROP COLUMN staging_token;
            DROP TABLE download_upgrade_admission_gate;
            DELETE FROM download_schema_migrations WHERE version IN (4, 5, 6, 7);
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
