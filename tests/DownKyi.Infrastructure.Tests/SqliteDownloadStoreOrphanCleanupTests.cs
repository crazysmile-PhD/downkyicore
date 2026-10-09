using System.Collections.Immutable;
using DownKyi.Application.Downloads;
using DownKyi.Application.Time;
using DownKyi.Domain.Downloads;
using DownKyi.Infrastructure.Downloads;
using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Tests;

public sealed class SqliteDownloadStoreOrphanCleanupTests : IDisposable
{
    private readonly SqliteDownloadStoreFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task LegacySucceedStatusStillInDownloadingTableIsQueuedForRecovery()
    {
        await _fixture.CreateLegacyDatabaseAsync();
        await _fixture.SetLegacyDownloadStatusAsync(5);
        using var store = _fixture.CreateStore();

        var restored = Assert.Single(
            await store.GetUnfinishedAsync(TestContext.Current.CancellationToken));

        Assert.Equal(DownloadPhase.Queued, restored.Phase);
    }

    [Fact]
    public async Task OrphanedLegacyDownloadingRecordIsDeletedDuringInitialization()
    {
        await _fixture.CreateLegacyDatabaseAsync();
        await _fixture.InsertOrphanedLegacyDownloadingRecordAsync();
        using var store = _fixture.CreateStore();

        var restored = await store.GetUnfinishedAsync(TestContext.Current.CancellationToken);

        Assert.Equal("legacy-resume", Assert.Single(restored).Id.Value);
        Assert.Equal(0, await _fixture.CountDownloadingRecordAsync("orphaned-download"));
    }

    [Fact]
    public async Task ValidDownloadingRecordIsPreservedDuringOrphanCleanup()
    {
        using (var store = _fixture.CreateStore())
        {
            Assert.True((await store.AddAsync(
                _fixture.CreatePausedTask("valid-download"),
                TestContext.Current.CancellationToken)).IsSuccess);
        }

        await _fixture.InsertOrphanedLegacyDownloadingRecordAsync();
        using var reopened = _fixture.CreateStore();
        var restored = Assert.Single(
            await reopened.GetUnfinishedAsync(TestContext.Current.CancellationToken));

        Assert.Equal("valid-download", restored.Id.Value);
        Assert.Equal(1, await _fixture.CountDownloadBaseRecordAsync("valid-download"));
        Assert.Equal(1, await _fixture.CountDownloadingRecordAsync("valid-download"));
        Assert.Equal(0, await _fixture.CountDownloadingRecordAsync("orphaned-download"));
    }

    [Fact]
    public async Task OrphanCleanupIsIdempotent()
    {
        await _fixture.CreateLegacyDatabaseAsync();
        await _fixture.InsertOrphanedLegacyDownloadingRecordAsync();
        using (var first = _fixture.CreateStore())
        {
            await first.InitializeAsync(TestContext.Current.CancellationToken);
        }

        using (var second = _fixture.CreateStore())
        {
            await second.InitializeAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(0, await _fixture.CountDownloadingRecordAsync("orphaned-download"));
        Assert.Equal(1, await _fixture.CountDownloadBaseRecordAsync("legacy-resume"));
        Assert.Equal(1, await _fixture.CountDownloadingRecordAsync("legacy-resume"));
    }

    [Fact]
    public async Task CurrentSchemaDatabaseStillCleansOrphanedDownloadingRecords()
    {
        using (var store = _fixture.CreateStore())
        {
            await store.InitializeAsync(TestContext.Current.CancellationToken);
        }

        await _fixture.InsertOrphanedLegacyDownloadingRecordAsync();
        using var reopened = _fixture.CreateStore();
        await reopened.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DownloadStoreSchema.CurrentVersion, await _fixture.ReadSchemaVersionAsync());
        Assert.Equal(0, await _fixture.CountDownloadingRecordAsync("orphaned-download"));
    }

    [Fact]
    public async Task MigratedLegacyDatabaseCleansOrphanedDownloadingRecords()
    {
        await _fixture.CreateLegacyDatabaseAsync();
        await _fixture.InsertOrphanedLegacyDownloadingRecordAsync();
        using var store = _fixture.CreateStore();

        await store.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DownloadStoreSchema.CurrentVersion, await _fixture.ReadSchemaVersionAsync());
        Assert.Equal(1, await _fixture.CountDownloadBaseRecordAsync("legacy-resume"));
        Assert.Equal(1, await _fixture.CountDownloadingRecordAsync("legacy-resume"));
        Assert.Equal(0, await _fixture.CountDownloadingRecordAsync("orphaned-download"));
    }

    [Fact]
    public async Task OrphanCleanupDoesNotAffectDownloadHistory()
    {
        using (var store = _fixture.CreateStore())
        {
            Assert.True((await store.AddHistoryAsync(
                DownloadHistoryRecord.FromCompletedTask(
                    _fixture.CreateCompletedTask("history-preserved", 123)),
                TestContext.Current.CancellationToken)).IsSuccess);
        }

        await _fixture.InsertOrphanedLegacyDownloadingRecordAsync();
        using var reopened = _fixture.CreateStore();
        var history = await reopened.GetHistoryPageAsync(null, 10, TestContext.Current.CancellationToken);

        Assert.Equal("history-preserved", Assert.Single(history.Items).Id.Value);
        Assert.Equal(0, await _fixture.CountDownloadBaseRecordAsync("history-preserved"));
        Assert.Equal(1, await _fixture.CountHistoryRecordAsync("history-preserved"));
        Assert.Equal(0, await _fixture.CountDownloadingRecordAsync("orphaned-download"));
    }
}
