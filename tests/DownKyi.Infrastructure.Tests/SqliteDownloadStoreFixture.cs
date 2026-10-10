using System.Collections.Immutable;
using DownKyi.Application.Downloads;
using DownKyi.Application.Time;
using DownKyi.Domain.Downloads;
using DownKyi.Infrastructure.Downloads;
using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Tests;

internal sealed partial class SqliteDownloadStoreFixture : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "downkyi-download-store-tests",
        Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 7, 13, 1, 2, 3, TimeSpan.Zero));

    internal string TempDirectory => _directory;

    internal IClock Clock => _clock;

    public void Dispose()
    {
        ClearStorePool();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    internal void ClearStorePool()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "download.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 5
        }.ToString());
        SqliteConnection.ClearPool(connection);
    }

    internal SqliteDownloadTaskStore CreateStore(
        IPhysicalOutputPathResolver? resolver = null,
        IClock? clock = null)
    {
        Directory.CreateDirectory(_directory);
        return new SqliteDownloadTaskStore(
            new SqliteDownloadTaskStoreOptions(Path.Combine(_directory, "download.db")),
            clock ?? _clock,
            resolver ?? new StubPhysicalOutputPathResolver(static path => path));
    }

    internal DownloadTask CreatePausedTask(
        string id,
        string? outputPath = null,
        DownloadNfoRequest? nfoRequest = null,
        DownloadContentSelection? requestedContent = null,
        long cid = 2)
    {
        var task = DownloadTask.Create(
            new DownloadTaskId(id),
            CreateMetadata(id, cid),
            CreatePlan(nfoRequest, requestedContent),
            new DownloadOutput(outputPath ?? Path.Combine(_directory, id), "1 GB"),
            _clock.UtcNow);
        task = task.Start(_clock.UtcNow.AddSeconds(1)).RequireValue();
        task = task.UpdateTransferState(
            new DownloadTransferState("aria-gid", ["cover"], "video", "Paused", 4_000_000),
            _clock.UtcNow.AddSeconds(2)).RequireValue();
        task = task.UpdateProgressAndTransfer(
            new DownloadProgress(42.5, 425, 1000, 3_000_000, "425 B", "24 Mbps"),
            task.Transfer,
            _clock.UtcNow.AddSeconds(3)).RequireValue();
        task = task.Pause(_clock.UtcNow.AddSeconds(4)).RequireValue();
        return task.ConfirmPaused(_clock.UtcNow.AddSeconds(5)).RequireValue();
    }

    internal DownloadTask CreateQueuedTask(
        string id,
        string outputPath,
        DownloadContentSelection? requestedContent = null,
        DownloadNfoRequest? nfoRequest = null,
        long cid = 2)
    {
        return DownloadTask.Create(
            new DownloadTaskId(id),
            CreateMetadata(id, cid),
            CreatePlan(nfoRequest, requestedContent),
            new DownloadOutput(outputPath, null),
            _clock.UtcNow);
    }

    internal DownloadTask CreateCompletedTask(
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

    internal static DownloadTaskMetadata CreateMetadata(string name, long cid = 2)
    {
        return new DownloadTaskMetadata(
            new DownloadMediaIdentity("BV1TEST", 1, cid, 3, 1, 1),
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

    internal static DownloadPlan CreatePlan(
        DownloadNfoRequest? nfoRequest = null,
        DownloadContentSelection? requestedContent = null)
    {
        return new DownloadPlan(
            requestedContent ?? new DownloadContentSelection(false, true, false, false, false),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["video"] = "video.m4s" },
            1,
            nfoRequest);
    }

    internal static DownloadNfoRequest CreateNfoRequest()
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

    internal sealed class TestClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            return Task.Delay(delay, cancellationToken);
        }
    }

    internal sealed class CancelOnReadClock(
        DateTimeOffset utcNow,
        CancellationTokenSource cancellation) : IClock
    {
        public DateTimeOffset UtcNow
        {
            get
            {
                cancellation.Cancel();
                return utcNow;
            }
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
            Task.Delay(delay, cancellationToken);
    }

    internal sealed class StubPhysicalOutputPathResolver(Func<string, string> resolve)
        : IPhysicalOutputPathResolver
    {
        public string ResolvePhysicalBasePath(string logicalBasePath) => resolve(logicalBasePath);
    }

    internal sealed record StoredDownloadState(
        string Path,
        string? Gid,
        string? DownloadFiles,
        string? DownloadedFiles,
        long? FinishedTimestamp);
}
