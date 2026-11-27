using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
using DownKyi.Infrastructure.Storage.Database;
using DownKyi.Infrastructure.Time;
using DownKyi.Models;
using DownKyi.Services.Migration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class LegacyDatabaseImportTests : IDisposable
{
    private static readonly string LegacyKey = LegacyUpgradeCoordinator.GetLegacyDatabasePassword();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "downkyi-legacy-import", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedImportPreservesAnEncryptedOrPlainSnapshotIncludingWal(bool encrypted)
    {
        var path = CreateDatabase(encrypted, missingTable: false);
        Assert.True(File.Exists(path + "-wal"));
        var history = new HistoryService();
        var progress = new ProgressRecorder();

        var result = await CreateCoordinator(history).UpgradeDatabaseAsync(path, progress, TestContext.Current.CancellationToken);

        Assert.Equal(LegacyUpgradeOutcome.Completed, result.Outcome);
        var imported = Assert.Single(history.Records);
        Assert.Equal("legacy-01", imported.Id.Value);
        Assert.Equal("Series", imported.MainTitle);
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + "-wal"));
        Assert.False(File.Exists(path + "-shm"));
        var backup = Assert.Single(Directory.GetFiles(Path.Combine(_directory, "Backup"), "*.db"));
        using var database = new SqliteDatabase(backup, encrypted ? LegacyKey : null);
        database.ExecuteQuery(command => command.CommandText = "SELECT value FROM wal_evidence", reader =>
        {
            Assert.True(reader.Read());
            Assert.Equal("only-in-wal", reader.GetString(0));
        });
        database.ExecuteQuery(command => command.CommandText = "SELECT COUNT(*) FROM downloaded", reader =>
        {
            Assert.True(reader.Read());
            Assert.Equal(1, reader.GetInt32(0));
        });
        Assert.Equal(encrypted ? 0 : 1, progress.Messages.Count(message => message == "尝试备用连接方式"));
    }

    [Fact]
    public async Task MissingTablesKeepTheirCauseWithoutPlaintextRetryAndPreserveSourceAndWal()
    {
        var path = CreateDatabase(encrypted: true, missingTable: true);
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var wal = await File.ReadAllBytesAsync(path + "-wal", TestContext.Current.CancellationToken);
        var progress = new ProgressRecorder();
        var history = new HistoryService();

        var result = await CreateCoordinator(history).UpgradeDatabaseAsync(path, progress, TestContext.Current.CancellationToken);

        Assert.Equal(LegacyUpgradeOutcome.Failed, result.Outcome);
        Assert.IsType<InvalidDataException>(result.Failure);
        Assert.Contains("downloaded", result.Failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("尝试备用连接方式", progress.Messages);
        Assert.Empty(history.Records);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(wal, await File.ReadAllBytesAsync(path + "-wal", TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Path.Combine(_directory, "Backup")));
    }

    [Fact]
    public async Task PersistenceFailurePreservesTheCompleteSource()
    {
        var path = CreateDatabase(encrypted: true, missingTable: false);
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var wal = await File.ReadAllBytesAsync(path + "-wal", TestContext.Current.CancellationToken);
        var history = new HistoryService
        {
            Write = (_, _) => Task.FromResult(OperationResult.Failure(new OperationError(
                "test.write.failed", "History write failed.", OperationErrorKind.Conflict)))
        };

        var result = await CreateCoordinator(history).UpgradeDatabaseAsync(
            path, new ProgressRecorder(), TestContext.Current.CancellationToken);

        Assert.Equal(LegacyUpgradeOutcome.Failed, result.Outcome);
        Assert.Equal("History write failed.", result.Failure?.Message);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(wal, await File.ReadAllBytesAsync(path + "-wal", TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Path.Combine(_directory, "Backup")));
    }

    [Fact]
    public async Task CancellationDuringPersistencePreservesTheCompleteSource()
    {
        var path = CreateDatabase(encrypted: true, missingTable: false);
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var wal = await File.ReadAllBytesAsync(path + "-wal", TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var history = new HistoryService
        {
            Write = async (_, token) =>
            {
                await cancellation.CancelAsync().ConfigureAwait(true);
                return await Task.FromCanceled<OperationResult>(token).ConfigureAwait(true);
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateCoordinator(history).UpgradeDatabaseAsync(
            path, new ProgressRecorder(), cancellation.Token));

        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(wal, await File.ReadAllBytesAsync(path + "-wal", TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Path.Combine(_directory, "Backup")));
    }

    [Fact]
    public async Task BackupFailureAfterPersistenceStillPreservesSourceAndWal()
    {
        var path = CreateDatabase(encrypted: true, missingTable: false);
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var wal = await File.ReadAllBytesAsync(path + "-wal", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_directory, "Backup"), "blocks backup directory creation", TestContext.Current.CancellationToken);
        var history = new HistoryService();

        var result = await CreateCoordinator(history).UpgradeDatabaseAsync(
            path, new ProgressRecorder(), TestContext.Current.CancellationToken);

        Assert.Equal(LegacyUpgradeOutcome.Failed, result.Outcome);
        Assert.IsAssignableFrom<IOException>(result.Failure);
        Assert.Single(history.Records);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(wal, await File.ReadAllBytesAsync(path + "-wal", TestContext.Current.CancellationToken));

        File.Delete(Path.Combine(_directory, "Backup"));
        var retried = await CreateCoordinator(history).UpgradeDatabaseAsync(
            path, new ProgressRecorder(), TestContext.Current.CancellationToken);
        Assert.Equal(LegacyUpgradeOutcome.Completed, retried.Outcome);
        Assert.Equal(2, history.Records.Count);
        Assert.Equal(history.Records[0].Id, history.Records[1].Id);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string CreateDatabase(bool encrypted, bool missingTable)
    {
        Directory.CreateDirectory(_directory);
        var seed = Path.Combine(_directory, "seed.db");
        using (var writer = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = encrypted ? new Uri(seed).AbsoluteUri + "?cipher=sqlcipher&legacy=4" : seed,
            Password = encrypted ? LegacyKey : string.Empty,
            Pooling = false
        }.ToString()))
        {
            writer.Open();
            using var command = writer.CreateCommand();
            command.CommandText = """
                CREATE TABLE download_base (id TEXT PRIMARY KEY, data BLOB);
                CREATE TABLE wal_evidence (value TEXT);
                PRAGMA journal_mode=WAL;
                PRAGMA wal_autocheckpoint=0;
                INSERT INTO wal_evidence VALUES ('only-in-wal');
                """;
            command.ExecuteNonQuery();
            if (!missingTable)
            {
                command.CommandText = """
                    CREATE TABLE downloaded (id TEXT PRIMARY KEY, data BLOB);
                    INSERT INTO downloaded VALUES ('legacy-01', @downloaded);
                    INSERT INTO download_base VALUES ('legacy-01', @base);
                    """;
                command.Parameters.AddWithValue("@downloaded", EncodeRecord(typeof(Downloaded),
                    [("MaxSpeedDisplay", "25 MB/s"), ("FinishedTime", "finished"), ("FinishedTimestamp", 1_700_000_000L)]));
                command.Parameters.AddWithValue("@base", EncodeRecord(typeof(DownloadBase),
                    [("NeedDownloadContent", null), ("Bvid", "BV1fixture"), ("Avid", 1L), ("Cid", 2L),
                     ("EpisodeId", 3L), ("CoverUrl", ""), ("PageCoverUrl", ""), ("ZoneId", 1), ("Order", 1),
                     ("MainTitle", "Series"), ("Name", "Episode"), ("Duration", "01:00"), ("VideoCodecName", "AVC"),
                     ("Resolution", null), ("AudioCodec", null), ("FilePath", "fixture"), ("FileSize", "1 GB"), ("Page", 1)]));
                command.ExecuteNonQuery();
            }
            // Copy an idle, committed WAL fixture before closing its writer (which
            // would checkpoint it). The import then runs without a live writer.
            var path = Path.Combine(_directory, "Download.db");
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                File.Copy(seed + suffix, path + suffix);
            }
            return path;
        }
    }

    // Minimal NRBF ClassWithMembersAndTypes records keep the fixture portable
    // without enabling BinaryFormatter. Member types follow the legacy DTOs.
    private static byte[] EncodeRecord(Type type, (string Name, object? Value)[] members)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)0);
        writer.Write(1);
        writer.Write(-1);
        writer.Write(1);
        writer.Write(0);
        writer.Write((byte)12);
        writer.Write(2);
        writer.Write(type.Assembly.FullName!);
        writer.Write((byte)5);
        writer.Write(1);
        writer.Write(type.FullName!);
        writer.Write(members.Length);
        foreach (var member in members)
        {
            writer.Write($"<{member.Name}>k__BackingField");
        }
        foreach (var member in members)
        {
            writer.Write(member.Value switch { string => (byte)1, null => (byte)2, _ => (byte)0 });
        }
        foreach (var member in members)
        {
            if (member.Value is long) writer.Write((byte)9);
            else if (member.Value is int) writer.Write((byte)8);
        }
        writer.Write(2);
        var objectId = 3;
        foreach (var member in members)
        {
            switch (member.Value)
            {
                case string value:
                    writer.Write((byte)6);
                    writer.Write(objectId++);
                    writer.Write(value);
                    break;
                case long value:
                    writer.Write(value);
                    break;
                case int value:
                    writer.Write(value);
                    break;
                default:
                    writer.Write((byte)10);
                    break;
            }
        }
        writer.Write((byte)11);
        writer.Flush();
        return stream.ToArray();
    }

    private static LegacyUpgradeCoordinator CreateCoordinator(HistoryService history) =>
        new(history, new SystemClock(), NullLogger<LegacyUpgradeCoordinator>.Instance);

    private sealed class ProgressRecorder : IProgress<LegacyUpgradeProgress>
    {
        internal List<string> Messages { get; } = [];
        public void Report(LegacyUpgradeProgress value) => Messages.Add(value.Message);
    }

    private sealed class HistoryService : IDownloadHistoryService
    {
        internal List<DownloadHistoryRecord> Records { get; } = [];
        internal Func<DownloadHistoryRecord, CancellationToken, Task<OperationResult>>? Write { get; init; }
        public Task<OperationResult> AddAsync(DownloadHistoryRecord history, CancellationToken cancellationToken)
        {
            Records.Add(history);
            return Write?.Invoke(history, cancellationToken) ?? Task.FromResult(OperationResult.Success());
        }
        public Task<OperationResult> CompleteAsync(DownloadTask completedTask, long expectedVersion, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DownloadHistoryPage> GetPageAsync(DownloadHistoryCursor? cursor, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<DownloadHistoryRecord>> GetByCidAsync(long cid, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<DownloadHistoryRecord>> GetByCidsAsync(IReadOnlyCollection<long> cids, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<OperationResult> DeleteAsync(DownloadTaskId taskId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<OperationResult> ClearAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
