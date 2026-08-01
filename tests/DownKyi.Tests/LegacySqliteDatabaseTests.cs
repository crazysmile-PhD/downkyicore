using DownKyi.Core.Storage.Database;
using DownKyi.Services.Migration;

namespace DownKyi.Tests;

public sealed class LegacySqliteDatabaseTests
{
    [Fact]
    public void QueryCompletionReleasesDatabaseFileForBackup()
    {
        using var directory = new TemporaryDirectory();
        var databasePath = Path.Combine(directory.Path, "Download.db");
        var backupPath = Path.Combine(directory.Path, "Download_failed.db");

        using (var database = new SqliteDatabase(databasePath))
        {
            database.ExecuteQuery(
                command => command.CommandText = "SELECT name FROM sqlite_master",
                reader =>
                {
                    while (reader.Read())
                    {
                    }
                });
        }

        File.Move(databasePath, backupPath);

        Assert.False(File.Exists(databasePath));
        Assert.True(File.Exists(backupPath));
    }

    [Fact]
    public void EmptyDatabaseDoesNotRequireLegacyMigration()
    {
        using var directory = new TemporaryDirectory();
        var databasePath = Path.Combine(directory.Path, "Download.db");

        using var database = new SqliteDatabase(databasePath);

        Assert.False(LegacyUpgradeCoordinator.HasLegacyDownloadSchema(database));
    }

    [Fact]
    public void EncryptedEmptyDatabaseIsReleasedAfterSchemaCheck()
    {
        using var directory = new TemporaryDirectory();
        var databasePath = Path.Combine(directory.Path, "Download.db");
        var backupPath = Path.Combine(directory.Path, "Download_failed.db");

        using (var database = new SqliteDatabase(
                   databasePath,
                   "bdb8eb69-3698-4af9-b722-9312d0fba623"))
        {
            Assert.False(LegacyUpgradeCoordinator.HasLegacyDownloadSchema(database));
        }

        File.Move(databasePath, backupPath);

        Assert.True(File.Exists(backupPath));
    }

    [Fact]
    public void DownloadTablesAreRecognizedAsLegacySchema()
    {
        using var directory = new TemporaryDirectory();
        var databasePath = Path.Combine(directory.Path, "Download.db");

        using var database = new SqliteDatabase(databasePath);
        database.ExecuteQuery(
            command => command.CommandText = """
                CREATE TABLE downloaded (id TEXT PRIMARY KEY, data BLOB NOT NULL);
                CREATE TABLE download_base (id TEXT PRIMARY KEY, data BLOB NOT NULL);
                SELECT 1;
                """,
            reader =>
            {
                while (reader.Read())
                {
                }
            });

        Assert.True(LegacyUpgradeCoordinator.HasLegacyDownloadSchema(database));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"DownKyi.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
