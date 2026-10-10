using System.Collections.Immutable;
using DownKyi.Application.Downloads;
using DownKyi.Application.Time;
using DownKyi.Domain.Downloads;
using DownKyi.Infrastructure.Downloads;
using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Tests;

public sealed class SqliteDownloadStoreReservationTests : IDisposable
{
    private readonly SqliteDownloadStoreFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task ConcurrentAddsAtomicallyClaimOneOutputPath()
    {
        var outputPath = Path.Combine(_fixture.TempDirectory, "shared-output");
        var first = _fixture.CreateQueuedTask("claim-first", outputPath);
        var second = _fixture.CreateQueuedTask("claim-second", outputPath);
        using var firstStore = _fixture.CreateStore();
        using var secondStore = _fixture.CreateStore();

        var results = await Task.WhenAll(
            firstStore.AddAsync(first, TestContext.Current.CancellationToken),
            secondStore.AddAsync(second, TestContext.Current.CancellationToken));

        Assert.Single(results, result => result.IsSuccess);
        var rejected = Assert.Single(results, result => !result.IsSuccess);
        Assert.Equal("download.store.output_path_reserved", rejected.Error?.Code);
    }

    [Fact]
    public async Task DisjointActionsCanAtomicallyShareOneOutputBasePath()
    {
        var outputPath = Path.Combine(_fixture.TempDirectory, "shared-action-output");
        var media = _fixture.CreateQueuedTask(
            "media-claim",
            outputPath,
            DownloadContentSelection.None with { Video = true });
        var subtitle = _fixture.CreateQueuedTask(
            "subtitle-claim",
            outputPath,
            DownloadContentSelection.None with
            {
                Subtitle = true,
                SelectedSubtitleTrackIds = [11]
            });
        using var store = _fixture.CreateStore();

        Assert.True((await store.AddAsync(
            media,
            TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await store.AddAsync(
            subtitle,
            TestContext.Current.CancellationToken)).IsSuccess);

        var unfinished = await store.GetUnfinishedAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, unfinished.Count);
        Assert.All(unfinished, task => Assert.Equal(outputPath, task.Output.BasePath));
    }

    [Fact]
    public async Task SubtitleClaimsRemainExclusiveBecauseFileNamesAreLanguageDerived()
    {
        var outputPath = Path.Combine(_fixture.TempDirectory, "shared-subtitle-output");
        using var store = _fixture.CreateStore();
        var first = _fixture.CreateQueuedTask(
            "subtitle-first",
            outputPath,
            DownloadContentSelection.None with
            {
                Subtitle = true,
                SelectedSubtitleTrackIds = [11]
            });
        var second = _fixture.CreateQueuedTask(
            "subtitle-second",
            outputPath,
            DownloadContentSelection.None with
            {
                Subtitle = true,
                SelectedSubtitleTrackIds = [22]
            });

        Assert.True((await store.AddAsync(
            first,
            TestContext.Current.CancellationToken)).IsSuccess);
        var rejected = await store.AddAsync(second, TestContext.Current.CancellationToken);

        Assert.False(rejected.IsSuccess);
        Assert.Equal("download.store.output_path_reserved", rejected.Error?.Code);
    }

    [Fact]
    public async Task DistinctDanmakuFormatsCanShareOneOutputBasePath()
    {
        var outputPath = Path.Combine(_fixture.TempDirectory, "shared-danmaku-output");
        using var store = _fixture.CreateStore();
        var ass = _fixture.CreateQueuedTask(
            "danmaku-ass",
            outputPath,
            DownloadContentSelection.None with
            {
                Danmaku = true,
                DanmakuOutputFormat = DownloadDanmakuOutputFormat.Ass
            });
        var xml = _fixture.CreateQueuedTask(
            "danmaku-xml",
            outputPath,
            DownloadContentSelection.None with
            {
                Danmaku = true,
                DanmakuOutputFormat = DownloadDanmakuOutputFormat.Xml
            });

        Assert.True((await store.AddAsync(
            ass,
            TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await store.AddAsync(
            xml,
            TestContext.Current.CancellationToken)).IsSuccess);
    }

    [Fact]
    public async Task NfoClaimsRemainExclusiveAcrossOtherwiseDisjointActions()
    {
        var outputPath = Path.Combine(_fixture.TempDirectory, "shared-nfo-output");
        using var store = _fixture.CreateStore();
        var media = _fixture.CreateQueuedTask(
            "nfo-media",
            outputPath,
            DownloadContentSelection.None with { Video = true },
            SqliteDownloadStoreFixture.CreateNfoRequest());
        var cover = _fixture.CreateQueuedTask(
            "nfo-cover",
            outputPath,
            DownloadContentSelection.None with { Cover = true },
            SqliteDownloadStoreFixture.CreateNfoRequest());

        Assert.True((await store.AddAsync(
            media,
            TestContext.Current.CancellationToken)).IsSuccess);
        var rejected = await store.AddAsync(cover, TestContext.Current.CancellationToken);

        Assert.False(rejected.IsSuccess);
        Assert.Equal("download.store.output_path_reserved", rejected.Error?.Code);
    }

    [Fact]
    public async Task CanceledOutputClaimIsReleasedOnlyWhenTaskIsDeleted()
    {
        var outputPath = Path.Combine(_fixture.TempDirectory, "cleanup-owned-output");
        var task = _fixture.CreateQueuedTask("cleanup-owner", outputPath);
        using var store = _fixture.CreateStore();
        Assert.True((await store.AddAsync(task, TestContext.Current.CancellationToken)).IsSuccess);
        var canceled = task.Cancel(_fixture.Clock.UtcNow.AddSeconds(1)).RequireValue();
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

        var deleted = canceled.Delete(_fixture.Clock.UtcNow.AddSeconds(2)).RequireValue();
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
        using var store = _fixture.CreateStore();
        var failed = _fixture.CreateQueuedTask("failed-snapshot", Path.Combine(_fixture.TempDirectory, "failed-output"));
        failed = failed.Start(_fixture.Clock.UtcNow.AddSeconds(1)).RequireValue();
        failed = failed.Fail(
            new DownloadFailure("download.failed", "Transfer failed.", true),
            _fixture.Clock.UtcNow.AddSeconds(2)).RequireValue();
        var paused = _fixture.CreatePausedTask("paused-snapshot");
        var completed = _fixture.CreateCompletedTask("completed-snapshot", 10);
        var quarantined = _fixture.CreateQueuedTask("quarantined-snapshot", Path.Combine(_fixture.TempDirectory, "quarantined"));
        foreach (var task in new[] { failed, paused, quarantined })
        {
            Assert.True((await store.AddAsync(task, TestContext.Current.CancellationToken)).IsSuccess);
        }
        Assert.True((await store.AddHistoryAsync(
            DownloadHistoryRecord.FromCompletedTask(completed),
            TestContext.Current.CancellationToken)).IsSuccess);

        await _fixture.InsertPreexistingQuarantineAsync(quarantined.Id.Value);
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
        var basePath = Path.Combine(_fixture.TempDirectory, "cafe\u0301-output");
        using var store = _fixture.CreateStore();
        Assert.True((await store.AddAsync(
            _fixture.CreateQueuedTask("legacy-null-key", basePath),
            TestContext.Current.CancellationToken)).IsSuccess);
        using (var connection = await _fixture.OpenConnectionAsync(readOnly: false))
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
        var decomposed = Path.Combine(_fixture.TempDirectory, "cafe\u0301-legacy");
        var composed = Path.Combine(_fixture.TempDirectory, "caf\u00e9-legacy");
        using var store = _fixture.CreateStore();
        Assert.True((await store.AddAsync(
            _fixture.CreateQueuedTask("legacy-point-equivalence", decomposed),
            TestContext.Current.CancellationToken)).IsSuccess);
        using (var connection = await _fixture.OpenConnectionAsync(readOnly: false))
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
        var normal = Path.Combine(_fixture.TempDirectory, "normal-caf\u00e9");
        var legacy = Path.Combine(_fixture.TempDirectory, "legacy-cafe\u0301");
        var completed = Path.Combine(_fixture.TempDirectory, "completed-output");
        var quarantined = Path.Combine(_fixture.TempDirectory, "quarantined-output");
        using var store = _fixture.CreateStore();
        Assert.True((await store.AddAsync(
            _fixture.CreateQueuedTask("normal-equivalence", normal),
            TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await store.AddAsync(
            _fixture.CreateQueuedTask("legacy-equivalence", legacy),
            TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await store.AddHistoryAsync(
            DownloadHistoryRecord.FromCompletedTask(
                _fixture.CreateCompletedTask("completed-equivalence", 10, completed)),
            TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await store.AddAsync(
            _fixture.CreateQueuedTask("quarantined-equivalence", quarantined),
            TestContext.Current.CancellationToken)).IsSuccess);
        using (var connection = await _fixture.OpenConnectionAsync(readOnly: false))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE download_base SET output_reservation_key = NULL
                WHERE id = 'legacy-equivalence'
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        await _fixture.InsertPreexistingQuarantineAsync("quarantined-equivalence");

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
            (Path.Combine(_fixture.TempDirectory, "unrelated"), false)
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
}
