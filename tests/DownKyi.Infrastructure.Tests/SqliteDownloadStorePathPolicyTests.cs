using System.Collections.Immutable;
using DownKyi.Application.Downloads;
using DownKyi.Application.Time;
using DownKyi.Domain.Downloads;
using DownKyi.Infrastructure.Downloads;
using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Tests;

public sealed class SqliteDownloadStorePathPolicyTests : IDisposable
{
    private readonly SqliteDownloadStoreFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task UninterpretableForeignPathBlocksBeforeChangingExistingTask()
    {
        var original = Path.Combine(_fixture.TempDirectory, "native-path");
        using (var first = _fixture.CreateStore())
        {
            Assert.True((await first.AddAsync(_fixture.CreatePausedTask("foreign-path", original),
                TestContext.Current.CancellationToken)).IsSuccess);
        }
        var foreignPath = OperatingSystem.IsWindows() ? "/foreign/path" : @"C:\foreign\path";
        var originalKey = await _fixture.ReadReservationKeyAsync("foreign-path");
        await _fixture.SetPathAndReservationKeyAsync("foreign-path", foreignPath, originalKey);
        using var reopened = _fixture.CreateStore();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reopened.InitializeAsync(TestContext.Current.CancellationToken));
        Assert.Equal(foreignPath, (await _fixture.ReadStoredStateAsync("foreign-path")).Path);
        Assert.Equal(originalKey, await _fixture.ReadReservationKeyAsync("foreign-path"));
    }

    [Fact]
    public async Task NewUninterpretablePathIsRejectedBeforeItCanPoisonReopen()
    {
        var foreignPath = OperatingSystem.IsWindows() ? "/foreign/path" : @"C:\foreign\path";
        using var store = _fixture.CreateStore();
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.AddAsync(_fixture.CreateQueuedTask("new-foreign-path", foreignPath),
                TestContext.Current.CancellationToken));

        Assert.Equal(0, await _fixture.CountDownloadBaseRecordAsync("new-foreign-path"));
        var existing = _fixture.CreateQueuedTask("update-foreign-path",
            Path.Combine(_fixture.TempDirectory, "safe-existing"));
        Assert.True((await store.AddAsync(existing,
            TestContext.Current.CancellationToken)).IsSuccess);
        var changed = existing.UpdateOutput(new DownloadOutput(foreignPath, null),
            _fixture.Clock.UtcNow.AddSeconds(1)).RequireValue();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.UpdateAsync(changed, existing.Version,
                TestContext.Current.CancellationToken));
        Assert.Equal(existing.Output.BasePath,
            (await _fixture.ReadStoredStateAsync(existing.Id.Value)).Path);
    }

    [Fact]
    public async Task ForwardSlashUncPathFollowsCurrentPlatformPolicyAcrossReopen()
    {
        const string path = "//server/share/downkyi-output";
        using (var first = _fixture.CreateStore())
        {
            if (OperatingSystem.IsWindows())
            {
                Assert.True((await first.AddAsync(_fixture.CreateQueuedTask("slash-unc", path),
                    TestContext.Current.CancellationToken)).IsSuccess);
            }
            else
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    first.AddAsync(_fixture.CreateQueuedTask("slash-unc", path),
                        TestContext.Current.CancellationToken));
                Assert.True((await first.AddAsync(_fixture.CreatePausedTask("legacy-slash-unc",
                    Path.Combine(_fixture.TempDirectory, "safe-unc")),
                    TestContext.Current.CancellationToken)).IsSuccess);
            }
        }

        if (OperatingSystem.IsWindows())
        {
            using var reopened = _fixture.CreateStore();
            await reopened.InitializeAsync(TestContext.Current.CancellationToken);
            Assert.Equal(DownloadOutputPathKey.Create(path,
                DownloadOutputPathKey.UsesCaseInsensitiveComparison),
                await _fixture.ReadReservationKeyAsync("slash-unc"));
            Assert.True(await reopened.IsOutputPathReservedAsync(path,
                DownloadOutputPathKey.UsesCaseInsensitiveComparison,
                TestContext.Current.CancellationToken));
        }
        else
        {
            var originalKey = await _fixture.ReadReservationKeyAsync("legacy-slash-unc");
            await _fixture.SetPathAndReservationKeyAsync("legacy-slash-unc", path, originalKey);
            using var reopened = _fixture.CreateStore();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                reopened.InitializeAsync(TestContext.Current.CancellationToken));
            Assert.Equal(originalKey, await _fixture.ReadReservationKeyAsync("legacy-slash-unc"));
            Assert.Equal(0, await _fixture.CountDownloadBaseRecordAsync("slash-unc"));
        }
    }

    [Fact]
    public async Task ActiveUpdateRecomputesKeyAndUniqueRejectsEquivalentNewAdmission()
    {
        var original = Path.Combine(_fixture.TempDirectory, "update-original");
        var next = Path.Combine(_fixture.TempDirectory, "update-cafe\u0301");
        var equivalent = Path.Combine(_fixture.TempDirectory, "update-caf\u00e9");
        using var store = _fixture.CreateStore();
        var task = _fixture.CreateQueuedTask("update-path", original);
        Assert.True((await store.AddAsync(task, TestContext.Current.CancellationToken)).IsSuccess);
        await _fixture.SetReservationKeyAsync(task.Id.Value, null);
        var updated = task.UpdateOutput(new DownloadOutput(next, null),
            _fixture.Clock.UtcNow.AddSeconds(1)).RequireValue();
        Assert.True((await store.UpdateAsync(updated, task.Version,
            TestContext.Current.CancellationToken)).IsSuccess);
        Assert.Equal(DownloadOutputPathKey.Create(next,
            DownloadOutputPathKey.UsesCaseInsensitiveComparison),
            await _fixture.ReadReservationKeyAsync(task.Id.Value));
        Assert.False((await store.AddAsync(_fixture.CreateQueuedTask("update-duplicate", equivalent),
            TestContext.Current.CancellationToken)).IsSuccess);
    }
}
