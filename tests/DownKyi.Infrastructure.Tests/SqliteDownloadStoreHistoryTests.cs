using System.Collections.Immutable;
using DownKyi.Application.Downloads;
using DownKyi.Application.Settings;
using DownKyi.Application.Time;
using DownKyi.Domain.Downloads;
using DownKyi.Infrastructure.Downloads;
using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Tests;

public sealed class SqliteDownloadStoreHistoryTests : IDisposable
{
    private static readonly string[] PagingTitles = ["alpha", "alpha", "\U00010000", "\uE000", "omega", "alpha"];
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
    public async Task HistoryCandidateQueryReturnsOnlyTheRequestedCid()
    {
        using var store = _fixture.CreateStore();
        var expected = DownloadHistoryRecord.FromCompletedTask(
            _fixture.CreateCompletedTask("history-cid-match", 200, cid: 101));
        var unrelated = DownloadHistoryRecord.FromCompletedTask(
            _fixture.CreateCompletedTask("history-cid-other", 100, cid: 202));
        Assert.True((await store.AddHistoryAsync(
            expected,
            TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await store.AddHistoryAsync(
            unrelated,
            TestContext.Current.CancellationToken)).IsSuccess);

        var candidates = await store.GetHistoryByCidAsync(
            101,
            TestContext.Current.CancellationToken);

        Assert.Equal(expected.Id, Assert.Single(candidates).Id);
    }

    [Fact]
    public async Task HistoryBatchQueryReturnsAllRequestedCidsOnly()
    {
        using var store = _fixture.CreateStore();
        foreach (var cid in new long[] { 101, 202, 303 })
        {
            Assert.True((await store.AddHistoryAsync(
                DownloadHistoryRecord.FromCompletedTask(
                    _fixture.CreateCompletedTask($"history-{cid}", cid, cid: cid)),
                TestContext.Current.CancellationToken)).IsSuccess);
        }

        var candidates = await store.GetHistoryByCidsAsync(
            [101, 303],
            TestContext.Current.CancellationToken);

        Assert.Equal([101L, 303L], candidates.Select(record => record.Cid).Order().ToArray());
    }

    [Fact]
    public async Task HistoryCandidateQueryQuarantinesCorruptMatchingRecords()
    {
        using var store = _fixture.CreateStore();
        var valid = DownloadHistoryRecord.FromCompletedTask(
            _fixture.CreateCompletedTask("history-cid-valid", 200, cid: 101));
        var corrupt = DownloadHistoryRecord.FromCompletedTask(
            _fixture.CreateCompletedTask("history-cid-corrupt", 100, cid: 101));
        Assert.True((await store.AddHistoryAsync(
            valid,
            TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await store.AddHistoryAsync(
            corrupt,
            TestContext.Current.CancellationToken)).IsSuccess);
        await _fixture.CorruptHistoryPublishedArtifactsAsync("history-cid-corrupt");

        var candidates = await store.GetHistoryByCidAsync(
            101,
            TestContext.Current.CancellationToken);

        Assert.Equal(valid.Id, Assert.Single(candidates).Id);
        var quarantine = Assert.Single(
            await store.GetQuarantinedRecordsAsync(TestContext.Current.CancellationToken));
        Assert.Equal("history-cid-corrupt", quarantine.RecordId);
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

    [Theory]
    [InlineData(DownloadFinishedSort.DownloadAsc)]
    [InlineData(DownloadFinishedSort.DownloadDesc)]
    [InlineData(DownloadFinishedSort.Number)]
    public async Task EveryHistoryOrderPagesThroughTiesUnicodeAndCorruptRows(DownloadFinishedSort sort)
    {
        using var store = _fixture.CreateStore();
        var records = PagingTitles
            .Select((title, index) => new DownloadHistoryRecord(
                new DownloadTaskId($"ordered-{index}"), index + 1, 0, index % 2, title, "part", "00:01", "avc1",
                80, "1080P", "AAC", null, [], 100 + index % 2, "finished", null)).ToArray();
        foreach (var record in records)
            Assert.True((await store.AddHistoryAsync(record, TestContext.Current.CancellationToken)).IsSuccess);
        await _fixture.CorruptHistoryPublishedArtifactsAsync("ordered-5");
        var valid = records.Where(record => record.Id.Value != "ordered-5");
        var expected = sort switch
        {
            DownloadFinishedSort.DownloadAsc => valid.OrderBy(record => record.FinishedTimestamp)
                .ThenBy(record => record.Id.Value, StringComparer.Ordinal),
            DownloadFinishedSort.DownloadDesc => valid.OrderByDescending(record => record.FinishedTimestamp)
                .ThenByDescending(record => record.Id.Value, StringComparer.Ordinal),
            _ => valid.OrderBy(record => record.MainTitle, StringComparer.Ordinal).ThenBy(record => record.Order)
                .ThenBy(record => record.Id.Value, StringComparer.Ordinal)
        };
        var actual = new List<DownloadHistoryRecord>();
        DownloadHistoryCursor? cursor = null;
        do
        {
            var page = await store.GetHistoryPageAsync(cursor, 2, TestContext.Current.CancellationToken, sort);
            Assert.InRange(page.Items.Count, 0, 2);
            actual.AddRange(page.Items);
            Assert.InRange(actual.Count, 0, records.Length);
            cursor = page.NextCursor;
        } while (cursor != null);

        Assert.Equal(expected.Select(record => record.Id), actual.Select(record => record.Id));
        Assert.Equal("ordered-5", Assert.Single(await store.GetQuarantinedRecordsAsync(
            TestContext.Current.CancellationToken)).RecordId);
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
