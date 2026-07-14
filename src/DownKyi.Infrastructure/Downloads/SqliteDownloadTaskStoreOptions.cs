namespace DownKyi.Infrastructure.Downloads;

public sealed class SqliteDownloadTaskStoreOptions
{
    public SqliteDownloadTaskStoreOptions(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
    }

    public string DatabasePath { get; }

    public TimeSpan BusyTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
