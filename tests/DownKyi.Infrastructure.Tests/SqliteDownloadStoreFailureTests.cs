using System.Collections.Immutable;
using DownKyi.Application.Downloads;
using DownKyi.Application.Time;
using DownKyi.Domain.Downloads;
using DownKyi.Infrastructure.Downloads;
using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Tests;

public sealed class SqliteDownloadStoreFailureTests : IDisposable
{
    private readonly SqliteDownloadStoreFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Theory]
    [InlineData("""{"downloadVideo":true,"selectedSubtitleTrackIds":"bad"}""")]
    [InlineData("""{"downloadVideo":true,"selectedSubtitleTrackIds":["bad"]}""")]
    [InlineData("""{"downloadVideo":true,"defaultSubtitleTrackId":"bad"}""")]
    [InlineData("""{"downloadVideo":true,"mediaKind":"unknown"}""")]
    [InlineData("""{"downloadVideo":true,"mediaKind":7}""")]
    [InlineData("""{"downloadDanmaku":true,"danmakuOutputFormat":"ass"}""")]
    [InlineData("""{"downloadDanmaku":true,"danmakuOutputFormat":0}""")]
    [InlineData("""{"downloadDanmaku":true,"danmakuOutputFormat":4}""")]
    public async Task InvalidRequestedContentIsQuarantinedWithoutHidingValidRecords(string payload)
    {
        using var store = _fixture.CreateStore();
        Assert.True((await store.AddAsync(
            _fixture.CreatePausedTask("valid-selection"),
            TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await store.AddAsync(
            _fixture.CreatePausedTask("corrupt-selection"),
            TestContext.Current.CancellationToken)).IsSuccess);
        await _fixture.CorruptRequestedAssetsAsync("corrupt-selection", payload);

        var restored = Assert.Single(
            await store.GetUnfinishedAsync(TestContext.Current.CancellationToken));
        var quarantine = Assert.Single(
            await store.GetQuarantinedRecordsAsync(TestContext.Current.CancellationToken));

        Assert.Equal("valid-selection", restored.Id.Value);
        Assert.Equal("corrupt-selection", quarantine.RecordId);
        Assert.Equal("need_download_content", quarantine.FieldName);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("""{"Version":1,"Title":"x","Plot":"x","Year":"2026","Genres":null,"Tags":[],"Actors":[],"BilibiliId":null,"Premiered":"","Ratings":[]}""")]
    public async Task CorruptModernNfoRequestIsQuarantined(string payload)
    {
        using var store = _fixture.CreateStore();
        Assert.True((await store.AddAsync(
            _fixture.CreatePausedTask("valid-nfo-sibling"),
            TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await store.AddAsync(
            _fixture.CreatePausedTask("corrupt-nfo", nfoRequest: SqliteDownloadStoreFixture.CreateNfoRequest()),
            TestContext.Current.CancellationToken)).IsSuccess);
        await _fixture.ReplaceNfoRequestAsync("corrupt-nfo", payload);

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
    public async Task InitializationHonorsCancellationBeforeOpeningDatabase()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        using var store = _fixture.CreateStore();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.InitializeAsync(cancellation.Token));

        Assert.False(File.Exists(Path.Combine(_fixture.TempDirectory, "download.db")));
    }

    [Fact]
    public async Task UnsupportedLegacySchemaLeavesDatabaseUnchangedAndKeepsBackup()
    {
        await _fixture.CreateIncompatibleLegacyDatabaseAsync();
        using var store = _fixture.CreateStore();

        var failure = await Assert.ThrowsAsync<DownloadStoreSchemaMismatchException>(() =>
            store.InitializeAsync(TestContext.Current.CancellationToken));
        Assert.Contains("missing table: downloaded", failure.SchemaDifferences);

        using var connection = await _fixture.OpenReadOnlyConnectionAsync().ConfigureAwait(true);
        using var columns = connection.CreateCommand();
        columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('download_base') WHERE name = 'version'";
        Assert.Equal(0L, await columns.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(
            Path.Combine(_fixture.TempDirectory, "Backup"),
            "download.db.schema-v0-*.bak"));
    }

    [Fact]
    public async Task CorruptRecordIsQuarantinedWithoutHidingValidRecordsOrPrivateData()
    {
        const string sensitiveValue = "C:\\Users\\private-user\\Downloads\\secret";
        using var store = _fixture.CreateStore();
        Assert.True((await store.AddAsync(
            _fixture.CreatePausedTask("valid"),
            TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await store.AddAsync(
            _fixture.CreatePausedTask("corrupt"),
            TestContext.Current.CancellationToken)).IsSuccess);
        await _fixture.CorruptRequestedAssetsAsync("corrupt", sensitiveValue);

        var tasks = await store.GetUnfinishedAsync(TestContext.Current.CancellationToken);
        var quarantine = Assert.Single(
            await store.GetQuarantinedRecordsAsync(TestContext.Current.CancellationToken));

        Assert.Equal("valid", Assert.Single(tasks).Id.Value);
        Assert.Equal("corrupt", quarantine.RecordId);
        Assert.Equal("need_download_content", quarantine.FieldName);
        Assert.DoesNotContain(sensitiveValue, quarantine.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisposeDoesNotClearProviderPoolOwnedBySiblingConnections()
    {
        var databasePath = Path.Combine(_fixture.TempDirectory, "download.db");
        var store = _fixture.CreateStore();
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
        var store = _fixture.CreateStore();
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        store.Dispose();

        var operation = store.GetUnfinishedAsync(TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await operation.ConfigureAwait(true));

        Assert.Equal(typeof(SqliteDownloadTaskStore).FullName, exception.ObjectName);
    }
}
