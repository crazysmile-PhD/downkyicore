using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Tests;

public sealed class LegacySqlCipherCompatibilityTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "downkyi-sqlcipher-compatibility-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Sqlite3MultipleCiphersReadsLegacySqlCipherVersionFourDatabase()
    {
        Directory.CreateDirectory(_directory);
        var databasePath = Path.Combine(_directory, "legacy.db");
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "legacy-sqlcipher-v4.db"),
            databasePath);
        var uri = new Uri(databasePath).AbsoluteUri + "?cipher=sqlcipher&legacy=4";
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = uri,
            Mode = SqliteOpenMode.ReadWrite,
            Password = "downkyi-legacy-fixture-key",
            Pooling = false
        }.ToString());

        await connection.OpenAsync(TestContext.Current.CancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT title FROM legacy_download WHERE id = @id";
        command.Parameters.AddWithValue("@id", "fixture-01");

        Assert.Equal(
            "portable migration fixture",
            await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WrongPasswordCannotReadLegacyDatabase()
    {
        Directory.CreateDirectory(_directory);
        var databasePath = Path.Combine(_directory, "legacy-wrong-key.db");
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "legacy-sqlcipher-v4.db"),
            databasePath);
        var uri = new Uri(databasePath).AbsoluteUri + "?cipher=sqlcipher&legacy=4";
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = uri,
            Mode = SqliteOpenMode.ReadWrite,
            Password = "wrong-key",
            Pooling = false
        }.ToString());

        await Assert.ThrowsAsync<SqliteException>(async () =>
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master";
            await command.ExecuteScalarAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        });
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
