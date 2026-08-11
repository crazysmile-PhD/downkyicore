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
        Assert.Equal(3L, await version.ExecuteScalarAsync(TestContext.Current.CancellationToken));
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
        Assert.Equal("video.m4s", restored.Plan.TransferFiles["video"]);
        Assert.Equal("cover", Assert.Single(restored.Transfer.CompletedFileKeys));
        Assert.Equal(42.5, restored.Progress.Percentage);
        Assert.Single(Directory.GetFiles(
            Path.Combine(_directory, "Backup"),
            "download.db.schema-v0-*.bak"));
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

        Assert.Equal(3, await ReadSchemaVersionAsync());
        Assert.Equal(0, await CountDownloadingRecordAsync("orphaned-download"));
    }

    [Fact]
    public async Task MigratedLegacyDatabaseCleansOrphanedDownloadingRecords()
    {
        await CreateLegacyDatabaseAsync();
        await InsertOrphanedLegacyDownloadingRecordAsync();
        using var store = CreateStore();

        await store.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, await ReadSchemaVersionAsync());
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
        Assert.Equal(expected.Plan.TransferFiles, restored.Plan.TransferFiles);
        Assert.Equal(expected.Progress, restored.Progress);
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
    public async Task DisposeReleasesTheOwnedConnectionPool()
    {
        var databasePath = Path.Combine(_directory, "download.db");
        var store = CreateStore();
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        store.Dispose();
        File.Delete(databasePath);

        Assert.False(File.Exists(databasePath));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private SqliteDownloadTaskStore CreateStore()
    {
        Directory.CreateDirectory(_directory);
        return new SqliteDownloadTaskStore(
            new SqliteDownloadTaskStoreOptions(Path.Combine(_directory, "download.db")),
            _clock);
    }

    private DownloadTask CreatePausedTask(string id)
    {
        var task = DownloadTask.Create(
            new DownloadTaskId(id),
            CreateMetadata(id),
            CreatePlan(),
            new DownloadOutput(Path.Combine(_directory, id), "1 GB"),
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

    private DownloadTask CreateCompletedTask(string id, long finishedTimestamp)
    {
        var task = DownloadTask.Create(
            new DownloadTaskId(id),
            CreateMetadata(id),
            CreatePlan(),
            new DownloadOutput(Path.Combine(_directory, id), "1 GB"),
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

    private static DownloadPlan CreatePlan()
    {
        return new DownloadPlan(
            new Dictionary<string, bool>(StringComparer.Ordinal) { ["video"] = true },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["video"] = "video.m4s" },
            1);
    }

    private async Task<SqliteConnection> OpenReadOnlyConnectionAsync()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "download.db"),
            Mode = SqliteOpenMode.ReadOnly
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

    private async Task CorruptRequestedAssetsAsync(string id, string sensitiveValue)
    {
        using var connection = await OpenConnectionAsync(readOnly: false).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE download_base SET need_download_content = @value WHERE id = @id";
        command.Parameters.AddWithValue("@value", sensitiveValue);
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
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite
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
            Mode = SqliteOpenMode.ReadWriteCreate
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
            Mode = SqliteOpenMode.ReadWriteCreate
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
}
