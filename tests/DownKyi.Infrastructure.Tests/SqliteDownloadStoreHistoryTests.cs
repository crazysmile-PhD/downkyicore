using System.Collections.Immutable;
using DownKyi.Application.Downloads;
using DownKyi.Application.Time;
using DownKyi.Domain.Downloads;
using DownKyi.Infrastructure.Downloads;
using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Tests;

public sealed class SqliteDownloadStoreHistoryTests : IDisposable
{
    private readonly SqliteDownloadStoreFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task CompletedHistoryRetainsFinalizedRequestedContentAcrossRestart()
    {
        var expected = DownloadHistoryRecord.FromCompletedTask(
            _fixture.CreateCompletedTask("video-only-history", 123));
        using (var first = _fixture.CreateStore())
        {
            Assert.True((await first.AddHistoryAsync(
                expected,
                TestContext.Current.CancellationToken)).IsSuccess);
        }

        using var reopened = _fixture.CreateStore();
        var restored = Assert.Single((await reopened.GetHistoryPageAsync(
            null,
            10,
            TestContext.Current.CancellationToken)).Items);

        Assert.Equal(expected.RequestedContent, restored.RequestedContent);
        Assert.True(restored.RequestedContent?.Video);
        Assert.False(restored.RequestedContent?.Audio);
    }

    [Fact]
    public async Task CompletedHistoryRecordRoundTripsEveryFieldAcrossDatabaseReopen()
    {
        var media = Path.Combine(_fixture.TempDirectory, "published.flv");
        var subtitle = Path.Combine(_fixture.TempDirectory, "published.zh-Hant.srt");
        var task = DownloadTask.Create(
            new DownloadTaskId("published-reopen"),
            SqliteDownloadStoreFixture.CreateMetadata("published-reopen"),
            SqliteDownloadStoreFixture.CreatePlan(),
            new DownloadOutput(Path.Combine(_fixture.TempDirectory, "base-without-matching-suffix"), "1.25 GiB"),
            _fixture.Clock.UtcNow);
        task = task.Start(_fixture.Clock.UtcNow.AddSeconds(1)).RequireValue();
        var mediaPublishing = new DownloadPublishingArtifact(
            "media", Path.GetFileName(media), 3, new string('A', 64));
        task = task.BeginPublishingArtifact(mediaPublishing,
            _fixture.Clock.UtcNow.AddSeconds(2)).RequireValue();
        task = task.RecordPublishedArtifact(mediaPublishing, media,
            _fixture.Clock.UtcNow.AddSeconds(3)).RequireValue();
        var subtitlePublishing = new DownloadPublishingArtifact(
            "subtitle:zh-Hant", Path.GetFileName(subtitle), 3, new string('B', 64));
        task = task.BeginPublishingArtifact(subtitlePublishing,
            _fixture.Clock.UtcNow.AddSeconds(4)).RequireValue();
        task = task.RecordPublishedArtifact(subtitlePublishing, subtitle,
            _fixture.Clock.UtcNow.AddSeconds(5)).RequireValue();
        var active = task;
        var completed = task.Complete(
            new DownloadCompletion(123, "finished", "24 Mbps"),
            _fixture.Clock.UtcNow.AddSeconds(6)).RequireValue();
        var expected = DownloadHistoryRecord.FromCompletedTask(completed);
        using (var store = _fixture.CreateStore())
        {
            Assert.True((await store.AddAsync(
                active,
                TestContext.Current.CancellationToken)).IsSuccess);
            Assert.True((await store.CompleteAsync(
                completed,
                expected,
                active.Version,
                TestContext.Current.CancellationToken)).IsSuccess);
        }

        using var reopened = _fixture.CreateStore();
        var restored = Assert.Single((await reopened.GetHistoryPageAsync(
            null, 10, TestContext.Current.CancellationToken)).Items);

        Assert.Equivalent(expected, restored, strict: true);
        Assert.Null(await reopened.FindAsync(completed.Id, TestContext.Current.CancellationToken));
        Assert.Equal(0, await _fixture.CountDownloadBaseRecordAsync(completed.Id.Value));
        Assert.Equal(1, await _fixture.CountHistoryRecordAsync(completed.Id.Value));
    }

    [Fact]
    public async Task HistoryConflictRollsBackCompletedTransitionAndKeepsRecoverableTask()
    {
        var active = _fixture.CreateQueuedTask("completion-conflict", Path.Combine(_fixture.TempDirectory, "conflict"));
        active = active.Start(_fixture.Clock.UtcNow.AddSeconds(1)).RequireValue();
        var completed = active.Complete(
            new DownloadCompletion(123, "finished", null),
            _fixture.Clock.UtcNow.AddSeconds(2)).RequireValue();
        var history = DownloadHistoryRecord.FromCompletedTask(completed);
        using var store = _fixture.CreateStore();
        Assert.True((await store.AddAsync(
            active,
            TestContext.Current.CancellationToken)).IsSuccess);
        await _fixture.InsertHistoryBypassingOwnershipCheckAsync(history);

        var result = await store.CompleteAsync(
            completed,
            history,
            active.Version,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.store.conflict", result.Error?.Code);
        var restored = await store.FindAsync(active.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(restored);
        Assert.Equal(active.Version, restored.Version);
        Assert.Equal(active.Phase, restored.Phase);
        Assert.Equal(1, await _fixture.CountDownloadBaseRecordAsync(active.Id.Value));
        Assert.Equal(1, await _fixture.CountHistoryRecordAsync(active.Id.Value));
    }

    [Fact]
    public async Task HistoryInsertFailureRollsBackCompletedTransitionAndKeepsRecoverableTask()
    {
        var active = _fixture.CreateQueuedTask("completion-write-failure", Path.Combine(_fixture.TempDirectory, "failure"));
        active = active.Start(_fixture.Clock.UtcNow.AddSeconds(1)).RequireValue();
        var completed = active.Complete(
            new DownloadCompletion(123, "finished", null),
            _fixture.Clock.UtcNow.AddSeconds(2)).RequireValue();
        using var store = _fixture.CreateStore();
        Assert.True((await store.AddAsync(
            active,
            TestContext.Current.CancellationToken)).IsSuccess);
        await _fixture.CreateHistoryInsertFailureTriggerAsync();

        await Assert.ThrowsAsync<SqliteException>(() => store.CompleteAsync(
            completed,
            DownloadHistoryRecord.FromCompletedTask(completed),
            active.Version,
            TestContext.Current.CancellationToken));

        var restored = await store.FindAsync(active.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(restored);
        Assert.Equal(active.Version, restored.Version);
        Assert.Equal(active.Phase, restored.Phase);
        Assert.Equal(1, await _fixture.CountDownloadBaseRecordAsync(active.Id.Value));
        Assert.Equal(0, await _fixture.CountHistoryRecordAsync(active.Id.Value));
    }

    [Fact]
    public async Task HistoryUsesStableKeysetPagination()
    {
        using var store = _fixture.CreateStore();
        foreach (var (id, timestamp) in new[]
        {
            ("history-a", 100L),
            ("history-b", 300L),
            ("history-c", 200L)
        })
        {
            Assert.True((await store.AddHistoryAsync(
                DownloadHistoryRecord.FromCompletedTask(_fixture.CreateCompletedTask(id, timestamp)),
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
    public async Task CorruptHistoryDoesNotTruncateLaterKeysetPages()
    {
        using var store = _fixture.CreateStore();
        foreach (var (id, timestamp) in new[]
        {
            ("history-a", 400L),
            ("history-b", 300L),
            ("history-corrupt", 200L),
            ("history-c", 100L)
        })
        {
            Assert.True((await store.AddHistoryAsync(
                DownloadHistoryRecord.FromCompletedTask(_fixture.CreateCompletedTask(id, timestamp)),
                TestContext.Current.CancellationToken)).IsSuccess);
        }

        await _fixture.CorruptHistoryPublishedArtifactsAsync("history-corrupt");

        var first = await store.GetHistoryPageAsync(null, 2, TestContext.Current.CancellationToken);
        Assert.NotNull(first.NextCursor);
        var second = await store.GetHistoryPageAsync(
            first.NextCursor,
            2,
            TestContext.Current.CancellationToken);

        Assert.Equal(["history-a", "history-b"], first.Items.Select(item => item.Id.Value));
        Assert.Equal("history-c", Assert.Single(second.Items).Id.Value);
        Assert.Null(second.NextCursor);
        var quarantine = Assert.Single(
            await store.GetQuarantinedRecordsAsync(TestContext.Current.CancellationToken));
        Assert.Equal("download_history", quarantine.SourceTable);
        Assert.Equal("history-corrupt", quarantine.RecordId);
        Assert.Equal("published_artifacts", quarantine.FieldName);
    }
}
