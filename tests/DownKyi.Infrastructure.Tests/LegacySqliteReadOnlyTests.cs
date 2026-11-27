using DownKyi.Infrastructure.Storage.Database;
using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Tests;

public sealed class LegacySqliteReadOnlyTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "downkyi-legacy-readonly", Guid.NewGuid().ToString("N"));

    [Fact]
    public void QueriesShareOneConnectionAndCannotWriteTheSource()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "source.db");
        using (var writer = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            writer.Open();
            using var command = writer.CreateCommand();
            command.CommandText = "CREATE TABLE evidence (value TEXT); INSERT INTO evidence VALUES ('original');";
            command.ExecuteNonQuery();
        }
        var original = File.ReadAllBytes(path);
        using (var database = new SqliteDatabase(path))
        {
            database.ExecuteQuery(command => command.CommandText = "PRAGMA cache_size=-1234", _ => { });
            database.ExecuteQuery(command => command.CommandText = "PRAGMA cache_size", reader =>
            {
                Assert.True(reader.Read());
                Assert.Equal(-1234, reader.GetInt32(0));
            });
            var failure = Assert.Throws<SqliteException>(() => database.ExecuteQuery(
                command => command.CommandText = "DELETE FROM evidence", _ => { }));
            Assert.Equal(8, failure.SqliteErrorCode);
        }
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + "-wal"));
    }

    [Fact]
    public void MissingSourceIsNotCreated()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "missing.db");
        Assert.Throws<SqliteException>(() =>
        {
            using var database = new SqliteDatabase(path);
        });
        Assert.False(File.Exists(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
