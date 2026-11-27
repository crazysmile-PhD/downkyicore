using DownKyi.Infrastructure.Storage.Database;
using Microsoft.Data.Sqlite;

namespace DownKyi.Windows.Tests;

public sealed class LegacySqliteDatabaseFileReleaseTests : IDisposable
{
    private const string SecretKey = "legacy-file-release-key";
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "downkyi-legacy-sqlite-release-tests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisposedProductionHelperReleasesLegacyFileForImmediateMoveAndDelete(bool encrypted)
    {
        Directory.CreateDirectory(_directory);
        var databasePath = Path.Combine(_directory, encrypted ? "encrypted.db" : "plain.db");
        var movedPath = Path.Combine(_directory, encrypted ? "encrypted-moved.db" : "plain-moved.db");
        var secretKey = encrypted ? SecretKey : null;
        CreateLegacyDatabase(databasePath, secretKey);

        using (var database = CreateProductionHelper(databasePath, secretKey))
        {
            string? storedValue = null;
            database.ExecuteQuery(
                command => command.CommandText = "SELECT value FROM legacy_record",
                reader =>
                {
                    Assert.True(reader.Read());
                    storedValue = reader.GetString(0);
                });
            Assert.Equal("legacy-value", storedValue);
        }

        File.Move(databasePath, movedPath);
        File.Delete(movedPath);

        Assert.False(File.Exists(databasePath));
        Assert.False(File.Exists(movedPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedInitializationReleasesFileWithoutCollectionOrRetry(bool usePassword)
    {
        Directory.CreateDirectory(_directory);
        var databasePath = Path.Combine(_directory, "invalid.db");
        File.WriteAllText(databasePath, "not a SQLite database");
        var failure = Assert.Throws<SqliteException>(() =>
        {
            using var database = CreateProductionHelper(databasePath, usePassword ? SecretKey : null);
        });
        Assert.Equal(26, failure.SqliteErrorCode);
        var movedPath = databasePath + ".preserved";
        File.Move(databasePath, movedPath);
        File.Delete(movedPath);
    }

    [Fact]
    public void WrongPasswordReleasesEncryptedFileForImmediateMove()
    {
        Directory.CreateDirectory(_directory);
        var databasePath = Path.Combine(_directory, "encrypted.db");
        CreateLegacyDatabase(databasePath, SecretKey);
        Assert.Throws<SqliteException>(() =>
        {
            using var database = CreateProductionHelper(databasePath, "incorrect-key");
        });
        var movedPath = databasePath + ".preserved";
        File.Move(databasePath, movedPath);
        File.Delete(movedPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static SqliteDatabase CreateProductionHelper(string databasePath, string? secretKey) =>
        secretKey == null
            ? new SqliteDatabase(databasePath)
            : new SqliteDatabase(databasePath, secretKey);

    private static void CreateLegacyDatabase(string databasePath, string? secretKey)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = secretKey == null
                ? databasePath
                : new Uri(Path.GetFullPath(databasePath)).AbsoluteUri + "?cipher=sqlcipher&legacy=4",
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        };
        if (secretKey != null)
        {
            builder.Password = secretKey;
        }

        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE legacy_record (value TEXT NOT NULL);
            INSERT INTO legacy_record (value) VALUES ('legacy-value');
            """;
        command.ExecuteNonQuery();
    }
}
