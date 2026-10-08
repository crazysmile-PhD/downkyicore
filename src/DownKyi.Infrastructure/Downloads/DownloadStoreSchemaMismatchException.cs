namespace DownKyi.Infrastructure.Downloads;

public sealed class DownloadStoreSchemaMismatchException : InvalidOperationException
{
    public DownloadStoreSchemaMismatchException()
    {
    }

    public DownloadStoreSchemaMismatchException(string message)
        : base(message)
    {
    }

    public DownloadStoreSchemaMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal DownloadStoreSchemaMismatchException(
        int userVersion,
        IReadOnlyList<string> schemaDifferences)
        : base($"Download database schema {userVersion} has an unsupported or incomplete shape: " +
            string.Join("; ", schemaDifferences))
    {
        UserVersion = userVersion;
        SchemaDifferences = Array.AsReadOnly(schemaDifferences.ToArray());
    }

    public int UserVersion { get; }

    // These values contain only schema identifiers owned by the application, never database rows.
    public IReadOnlyList<string> SchemaDifferences { get; } = [];
}
