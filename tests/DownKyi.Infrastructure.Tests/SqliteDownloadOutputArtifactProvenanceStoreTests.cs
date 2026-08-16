using DownKyi.Application.Downloads;
using DownKyi.Application.Time;
using DownKyi.Domain.Downloads;
using DownKyi.Infrastructure.Downloads;
using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Tests;

public sealed class SqliteDownloadOutputArtifactProvenanceStoreTests : IDisposable
{
    private static readonly DateTimeOffset Epoch = new(2026, 8, 16, 0, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "downkyi-output-artifact-provenance-tests",
        Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock = new(Epoch);

    [Fact]
    public async Task PublishedProvenanceSurvivesRestart()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var task = CreateTask("restart-task");
        var expected = CreateProvenance(task, "cover", "cover", "cover-id");

        using (var store = CreateStore())
        {
            Assert.True((await store.AddAsync(task, cancellationToken)).IsSuccess);
            Assert.True((await store.RecordPublishedAsync(expected, cancellationToken)).IsSuccess);

            var beforeRestart = (await store.GetPublishedAsync(task.Id, cancellationToken)).RequireValue();
            AssertProvenance(expected, Assert.Single(beforeRestart));
        }

        using var reopened = CreateStore();
        var afterRestart = (await reopened.GetPublishedAsync(task.Id, cancellationToken)).RequireValue();

        AssertProvenance(expected, Assert.Single(afterRestart));
    }

    [Fact]
    public async Task VersionFourMigrationDoesNotBackfillFinalOutputProvenance()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var task = CreateTask("legacy-task");
        using (var store = CreateStore())
        {
            Assert.True((await store.AddAsync(task, cancellationToken)).IsSuccess);
        }

        using (var connection = await OpenReadWriteConnectionAsync())
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TABLE download_output_artifact_provenance;
                DELETE FROM download_schema_migrations WHERE version = 5;
                PRAGMA user_version = 4;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        using var migrated = CreateStore();
        var provenance = await migrated.GetPublishedAsync(task.Id, cancellationToken);
        var restored = await migrated.FindAsync(task.Id, cancellationToken);

        Assert.True(provenance.IsSuccess);
        Assert.Empty(provenance.RequireValue());
        Assert.NotNull(restored);
        Assert.Equal("video.m4s", restored.Plan.TransferFiles["video"]);
        Assert.Equal(5, await ReadSchemaVersionAsync());
    }

    [Fact]
    public async Task PublishedProvenanceUsesTaskAndArtifactKeyAsUniqueIdentity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var task = CreateTask("unique-task");
        var original = CreateProvenance(task, "cover", "cover", "original-cover");
        var conflict = CreateProvenance(task, "cover", "cover", "replacement-cover", hashCharacter: 'b');
        var second = CreateProvenance(task, "danmaku", "danmaku", "danmaku-id", hashCharacter: 'c');
        using var store = CreateStore();

        Assert.True((await store.AddAsync(task, cancellationToken)).IsSuccess);
        Assert.True((await store.RecordPublishedAsync(original, cancellationToken)).IsSuccess);

        var rejected = await store.RecordPublishedAsync(conflict, cancellationToken);

        Assert.False(rejected.IsSuccess);
        Assert.Equal("download.output_provenance.conflict", rejected.Error?.Code);
        Assert.True((await store.RecordPublishedAsync(second, cancellationToken)).IsSuccess);

        var persisted = (await store.GetPublishedAsync(task.Id, cancellationToken)).RequireValue();
        Assert.Equal(2, persisted.Count);
        AssertProvenance(original, Assert.Single(persisted, item => item.ArtifactKey == "cover"));
        AssertProvenance(second, Assert.Single(persisted, item => item.ArtifactKey == "danmaku"));
    }

    [Fact]
    public async Task PublishedProvenanceRequiresAnExistingTask()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var task = CreateTask("missing-task");
        using var store = CreateStore();

        var result = await store.RecordPublishedAsync(
            CreateProvenance(task, "cover", "cover", "cover-id"),
            cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.output_provenance.task_not_found", result.Error?.Code);
    }

    [Fact]
    public async Task TaskDeletionCascadesProvenanceAfterTheTaskIsRemoved()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var task = CreateTask("cascade-task");
        using var store = CreateStore();
        Assert.True((await store.AddAsync(task, cancellationToken)).IsSuccess);
        Assert.True((await store.RecordPublishedAsync(
            CreateProvenance(task, "cover", "cover", "cover-id"),
            cancellationToken)).IsSuccess);

        var canceled = task.Cancel(Epoch.AddSeconds(1)).RequireValue();
        Assert.True((await store.UpdateAsync(canceled, task.Version, cancellationToken)).IsSuccess);
        var deleted = canceled.Delete(Epoch.AddSeconds(2)).RequireValue();
        Assert.True((await store.UpdateAsync(deleted, canceled.Version, cancellationToken)).IsSuccess);

        var provenance = await store.GetPublishedAsync(task.Id, cancellationToken);
        Assert.True(provenance.IsSuccess);
        Assert.Empty(provenance.RequireValue());
    }

    [Fact]
    public async Task CorruptPersistedProvenanceFailsClosedInsteadOfReturningAnEmptyResult()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var task = CreateTask("corrupt-task");
        using (var store = CreateStore())
        {
            Assert.True((await store.AddAsync(task, cancellationToken)).IsSuccess);
            Assert.True((await store.RecordPublishedAsync(
                CreateProvenance(task, "cover", "cover", "cover-id"),
                cancellationToken)).IsSuccess);
        }

        using (var connection = await OpenReadWriteConnectionAsync())
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE download_output_artifact_provenance
                SET sha256 = 'not-a-digest'
                WHERE task_id = @task_id
                """;
            command.Parameters.AddWithValue("@task_id", task.Id.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        using var reopened = CreateStore();
        var result = await reopened.GetPublishedAsync(task.Id, cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.output_provenance.corrupt", result.Error?.Code);
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

    private DownloadTask CreateTask(string id)
    {
        return DownloadTask.Create(
            new DownloadTaskId(id),
            new DownloadTaskMetadata(
                new DownloadMediaIdentity("BV1TEST", 1, 2, 3, 1, 1),
                "Collection",
                id,
                "00:10",
                "AVC",
                new DownloadQuality(80, "1080P"),
                new DownloadQuality(30280, "192K"),
                "cover",
                "page-cover",
                0),
            new DownloadPlan(
                new Dictionary<string, bool>(StringComparer.Ordinal) { ["video"] = true },
                new Dictionary<string, string>(StringComparer.Ordinal) { ["video"] = "video.m4s" },
                1),
            new DownloadOutput(Path.Combine(_directory, id), null),
            Epoch);
    }

    private DownloadOutputArtifactProvenance CreateProvenance(
        DownloadTask task,
        string artifactKey,
        string artifactKind,
        string identity,
        char hashCharacter = 'a')
    {
        return new DownloadOutputArtifactProvenance(
            task.Id,
            artifactKey,
            artifactKind,
            Path.Combine(_directory, $"{task.Id.Value}-{artifactKey}.mp4"),
            new OutputArtifactPublicationEvidence(
                42,
                new string(hashCharacter, 64),
                "windows.file-id",
                identity),
            Epoch);
    }

    private static void AssertProvenance(
        DownloadOutputArtifactProvenance expected,
        DownloadOutputArtifactProvenance actual)
    {
        Assert.Equal(expected.TaskId, actual.TaskId);
        Assert.Equal(expected.ArtifactKey, actual.ArtifactKey);
        Assert.Equal(expected.ArtifactKind, actual.ArtifactKind);
        Assert.Equal(expected.CanonicalPath, actual.CanonicalPath);
        Assert.Equal(expected.ByteLength, actual.ByteLength);
        Assert.Equal(expected.Sha256, actual.Sha256);
        Assert.Equal(expected.IdentityProvider, actual.IdentityProvider);
        Assert.Equal(expected.FilesystemIdentity, actual.FilesystemIdentity);
        Assert.Equal(expected.PublishedAtUtc, actual.PublishedAtUtc);
    }

    private async Task<long> ReadSchemaVersionAsync()
    {
        using var connection = await OpenReadWriteConnectionAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken).ConfigureAwait(false))!;
    }

    private async Task<SqliteConnection> OpenReadWriteConnectionAsync()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "download.db"),
            Mode = SqliteOpenMode.ReadWrite
        }.ToString());
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        return connection;
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
