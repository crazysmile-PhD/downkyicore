using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Storage.Database;

/// <summary>
/// Owns one read-only snapshot of a legacy SQLite database for an import.
/// </summary>
public sealed class SqliteDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;
    private readonly string? _secretKey;

    public SqliteDatabase(string dbPath, string? secretKey = null)
    {
        _secretKey = secretKey;
        _connection = new SqliteConnection(CreateConnectionString(dbPath, secretKey, SqliteOpenMode.ReadOnly));
        try
        {
            _connection.Open();
            _transaction = _connection.BeginTransaction(deferred: true);
            // Opening SQLite alone does not always read a page. Probe the format
            // before schema validation, and pin the WAL snapshot for this import.
            ExecuteQuery(command => command.CommandText = "SELECT name FROM sqlite_master LIMIT 1", reader => reader.Read());
        }
        catch
        {
            _connection.Dispose();
            throw;
        }
    }

    public void ExecuteQuery(Action<SqliteCommand> configureCommand, Action<SqliteDataReader> readAction)
    {
        ArgumentNullException.ThrowIfNull(configureCommand);
        ArgumentNullException.ThrowIfNull(readAction);

        using var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        configureCommand(command);
        if (string.IsNullOrWhiteSpace(command.CommandText))
        {
            throw new InvalidOperationException("SQLite query command text must be configured.");
        }

        using var reader = command.ExecuteReader();
        readAction(reader);
    }

    public void CreateBackup(string backupPath)
    {
        // Claim the output so failure cleanup cannot overwrite or remove an
        // existing backup owned by another import.
        using (new FileStream(backupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
        }

        try
        {
            using var destination = new SqliteConnection(
                CreateConnectionString(backupPath, _secretKey, SqliteOpenMode.ReadWrite));
            destination.Open();
            // SQLite copies the pinned snapshot, including committed WAL pages.
            _connection.BackupDatabase(destination);
        }
        catch
        {
            File.Delete(backupPath);
            throw;
        }
    }

    public void Dispose()
    {
        try
        {
            _transaction.Dispose();
        }
        finally
        {
            _connection.Dispose();
        }
    }

    private static string CreateConnectionString(string path, string? secretKey, SqliteOpenMode mode) =>
        new SqliteConnectionStringBuilder
        {
            Mode = mode,
            DataSource = secretKey == null
                ? path
                : new Uri(Path.GetFullPath(path)).AbsoluteUri + "?cipher=sqlcipher&legacy=4",
            Password = secretKey ?? string.Empty,
            Pooling = false,
            DefaultTimeout = 30
        }.ToString();
}
