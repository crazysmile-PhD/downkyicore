using System.Collections.Immutable;
using DownKyi.Application.Downloads;
using DownKyi.Application.Time;
using DownKyi.Domain.Downloads;
using DownKyi.Infrastructure.Downloads;
using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Tests;

public sealed class SqliteDownloadStoreStateTests : IDisposable
{
    private readonly SqliteDownloadStoreFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task PausedTaskPreservesResumeStateAcrossReopen()
    {
        var expected = _fixture.CreatePausedTask("resume-01");
        using (var store = _fixture.CreateStore())
        {
            Assert.True((await store.AddAsync(expected, TestContext.Current.CancellationToken)).IsSuccess);
        }

        using var reopened = _fixture.CreateStore();
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
    public async Task PendingPublicationSurvivesDatabaseReopenWithoutBecomingPublished()
    {
        var task = DownloadTask.Create(
            new DownloadTaskId("publishing-reopen"),
            SqliteDownloadStoreFixture.CreateMetadata("publishing-reopen"),
            SqliteDownloadStoreFixture.CreatePlan(),
            new DownloadOutput(Path.Combine(_fixture.TempDirectory, "output"), null),
            _fixture.Clock.UtcNow);
        task = task.Start(_fixture.Clock.UtcNow.AddSeconds(1)).RequireValue();
        var publishing = new DownloadPublishingArtifact(
            "media", "output.mp4", 3, new string('A', 64));
        task = task.BeginPublishingArtifact(publishing, _fixture.Clock.UtcNow.AddSeconds(2))
            .RequireValue();
        Assert.False(task.Complete(new DownloadCompletion(123, "finished", null),
            _fixture.Clock.UtcNow.AddSeconds(3)).IsSuccess);
        using (var store = _fixture.CreateStore())
        {
            Assert.True((await store.AddAsync(task, TestContext.Current.CancellationToken)).IsSuccess);
        }

        using var reopened = _fixture.CreateStore();
        var restored = Assert.Single(
            await reopened.GetUnfinishedAsync(TestContext.Current.CancellationToken));
        Assert.Equal(publishing, restored.Output.PublishingArtifact);
        Assert.Empty(restored.Output.PublishedArtifacts);
        var canceled = restored.Cancel(restored.UpdatedAtUtc.AddSeconds(1)).RequireValue();
        var publishedAfterCancel = canceled.RecordPublishedArtifact(publishing,
            Path.Combine(_fixture.TempDirectory, publishing.FileName),
            canceled.UpdatedAtUtc.AddSeconds(1)).RequireValue();
        Assert.Equal(DownloadPhase.Canceled, publishedAfterCancel.Phase);
        Assert.Null(publishedAfterCancel.Output.PublishingArtifact);
    }

    [Fact]
    public async Task TypedNfoRequestRoundTripsAcrossReopen()
    {
        var expected = _fixture.CreatePausedTask("nfo-resume", nfoRequest: SqliteDownloadStoreFixture.CreateNfoRequest());
        using (var store = _fixture.CreateStore())
        {
            Assert.True((await store.AddAsync(expected, TestContext.Current.CancellationToken)).IsSuccess);
        }

        using var reopened = _fixture.CreateStore();
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
    public async Task TypedRequestedContentAndSubtitleSelectionRoundTripAcrossReopen()
    {
        var expected = new DownloadContentSelection(
            Audio: true,
            Video: false,
            Danmaku: true,
            Subtitle: true,
            Cover: true) with
        {
            MediaKind = DownloadMediaKind.Dash,
            SelectedSubtitleTrackIds = ImmutableArray.Create(11L, 22L),
            DefaultSubtitleTrackId = 22,
            DanmakuOutputFormat = DownloadDanmakuOutputFormat.Ass |
                                  DownloadDanmakuOutputFormat.Xml
        };
        using (var store = _fixture.CreateStore())
        {
            Assert.True((await store.AddAsync(
                _fixture.CreatePausedTask("typed-content", requestedContent: expected),
                TestContext.Current.CancellationToken)).IsSuccess);
        }

        using var reopened = _fixture.CreateStore();
        var restored = Assert.Single(
            await reopened.GetUnfinishedAsync(TestContext.Current.CancellationToken));

        var actual = restored.Plan.RequestedContent;
        Assert.Equal(expected.Audio, actual.Audio);
        Assert.Equal(expected.Video, actual.Video);
        Assert.Equal(expected.Danmaku, actual.Danmaku);
        Assert.Equal(expected.Subtitle, actual.Subtitle);
        Assert.Equal(expected.Cover, actual.Cover);
        Assert.Equal(expected.MediaKind, actual.MediaKind);
        Assert.True(expected.SelectedSubtitleTrackIds.HasValue);
        Assert.True(actual.SelectedSubtitleTrackIds.HasValue);
        Assert.Equal(
            expected.SelectedSubtitleTrackIds.GetValueOrDefault().ToArray(),
            actual.SelectedSubtitleTrackIds.GetValueOrDefault().ToArray());
        Assert.Equal(expected.DefaultSubtitleTrackId, actual.DefaultSubtitleTrackId);
        Assert.Equal(expected.DanmakuOutputFormat, actual.DanmakuOutputFormat);
        using var connection = await _fixture.OpenReadOnlyConnectionAsync().ConfigureAwait(true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT need_download_content FROM download_base WHERE id = 'typed-content'";
        var json = Assert.IsType<string>(
            await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        using var payload = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(9, payload.RootElement.EnumerateObject().Count());
        Assert.Equal("dash", payload.RootElement.GetProperty("mediaKind").GetString());
        Assert.Equal(3, payload.RootElement.GetProperty("danmakuOutputFormat").GetInt32());
        Assert.True(payload.RootElement.GetProperty("downloadAudio").GetBoolean());
        Assert.False(payload.RootElement.GetProperty("downloadVideo").GetBoolean());
        Assert.True(payload.RootElement.GetProperty("downloadDanmaku").GetBoolean());
        Assert.True(payload.RootElement.GetProperty("downloadSubtitle").GetBoolean());
        Assert.True(payload.RootElement.GetProperty("downloadCover").GetBoolean());
        Assert.Equal([11L, 22L], payload.RootElement.GetProperty("selectedSubtitleTrackIds")
            .EnumerateArray().Select(item => item.GetInt64()));
        Assert.Equal(22, payload.RootElement.GetProperty("defaultSubtitleTrackId").GetInt64());
    }

    [Fact]
    public async Task UpdateRejectsAStaleVersion()
    {
        var original = DownloadTask.Create(
            new DownloadTaskId("versioned"),
            SqliteDownloadStoreFixture.CreateMetadata("Versioned"),
            SqliteDownloadStoreFixture.CreatePlan(),
            new DownloadOutput("output", null),
            _fixture.Clock.UtcNow);
        using var store = _fixture.CreateStore();
        Assert.True((await store.AddAsync(original, TestContext.Current.CancellationToken)).IsSuccess);
        var started = original.Start(_fixture.Clock.UtcNow.AddSeconds(1)).RequireValue();
        Assert.True((await store.UpdateAsync(started, original.Version, TestContext.Current.CancellationToken)).IsSuccess);
        var paused = started.Pause(_fixture.Clock.UtcNow.AddSeconds(2)).RequireValue();

        var stale = await store.UpdateAsync(paused, original.Version, TestContext.Current.CancellationToken);

        Assert.False(stale.IsSuccess);
        Assert.Equal("download.store.conflict", stale.Error?.Code);
    }

    [Fact]
    public async Task CoalescedProgressWriteAdvancesVersionAndPayloadAtomically()
    {
        var original = DownloadTask.Create(
            new DownloadTaskId("progress"),
            SqliteDownloadStoreFixture.CreateMetadata("Progress"),
            SqliteDownloadStoreFixture.CreatePlan(),
            new DownloadOutput("output", null),
            _fixture.Clock.UtcNow).Start(_fixture.Clock.UtcNow.AddSeconds(1)).RequireValue();
        using var store = _fixture.CreateStore();
        Assert.True((await store.AddAsync(original, TestContext.Current.CancellationToken)).IsSuccess);
        var progress = new DownloadProgress(75, 750, 1000, 5_000_000, "750 B", "40 Mbps");

        var result = await store.UpdateProgressAsync(
            new DownKyi.Application.Downloads.DownloadProgressWrite(
                original.Id,
                progress,
                original.Version,
                original.Version + 3,
                _fixture.Clock.UtcNow.AddSeconds(4)),
            TestContext.Current.CancellationToken);
        var restored = await store.FindAsync(original.Id, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.NotNull(restored);
        Assert.Equal(original.Version + 3, restored.Version);
        Assert.Equal(progress, restored.Progress);
    }
}
