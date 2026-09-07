namespace DownKyi.Architecture.Tests;

public sealed class SqliteDownloadTaskStoreArchitectureTests
{
    private const string FacadeFile = "SqliteDownloadTaskStore.cs";
    private const string Database = "SqliteDownloadStoreDatabase";
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string[] SqlMarkers = ["SELECT ", "INSERT ", "UPDATE ", "DELETE ", "PRAGMA "];
    private static readonly string[] Collaborators =
    [
        "SqliteDownloadStoreQueries",
        "SqliteDownloadStoreCommands",
        "SqliteDownloadStoreOutputReservations",
        "SqliteDownloadStoreQuarantine"
    ];

    [Fact]
    public void FacadeIsNonPartialAndContainsNoSql()
    {
        var source = ReadDownloadSource(FacadeFile);

        Assert.Contains("public sealed class SqliteDownloadTaskStore", source, StringComparison.Ordinal);
        Assert.DoesNotContain("partial class SqliteDownloadTaskStore", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CommandText", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.Data.Sqlite", source, StringComparison.Ordinal);
        Assert.All(
            SqlMarkers,
            sql => Assert.DoesNotContain(sql, source, StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(Path.Combine(DownloadSourceRoot, "SqliteDownloadTaskStore.OutputReservations.cs")));
    }

    [Fact]
    public void FacadeDelegatesToExplicitCollaborators()
    {
        var source = ReadDownloadSource(FacadeFile);

        Assert.Contains($"new {Database}", source, StringComparison.Ordinal);
        Assert.All(
            Collaborators,
            collaborator => Assert.Contains($"new {collaborator}", source, StringComparison.Ordinal));
    }

    [Fact]
    public void CollaboratorsDependOnDatabaseWithoutPeerCoupling()
    {
        foreach (var collaborator in Collaborators)
        {
            var source = ReadDownloadSource($"{collaborator}.cs");
            Assert.Contains($"internal sealed class {collaborator}", source, StringComparison.Ordinal);
            Assert.Contains(Database, source, StringComparison.Ordinal);
            Assert.DoesNotContain($"partial class {collaborator}", source, StringComparison.Ordinal);

            var forbiddenPeers = Collaborators.Where(peer =>
                peer != collaborator &&
                !(collaborator == "SqliteDownloadStoreQueries" &&
                  peer == "SqliteDownloadStoreQuarantine"));
            Assert.All(
                forbiddenPeers,
                peer => Assert.DoesNotContain(peer, source, StringComparison.Ordinal));
        }

        var database = ReadDownloadSource($"{Database}.cs");
        Assert.All(
            Collaborators,
            collaborator => Assert.DoesNotContain(collaborator, database, StringComparison.Ordinal));
    }

    [Fact]
    public void DatabaseOwnsConnectionsInitializationAndStoreTransactionBoundaries()
    {
        var database = ReadDownloadSource($"{Database}.cs");
        Assert.Contains("new SqliteConnection", database, StringComparison.Ordinal);
        Assert.Contains("SemaphoreSlim", database, StringComparison.Ordinal);
        Assert.Contains("DownloadStoreSchema.InitializeAsync", database, StringComparison.Ordinal);
        Assert.Contains("RemoveOrphanedDownloadingRecordsAsync", database, StringComparison.Ordinal);
        Assert.Contains("BeginTransaction", database, StringComparison.Ordinal);
        Assert.Contains("CommitAsync", database, StringComparison.Ordinal);
        Assert.Contains("RollbackAsync", database, StringComparison.Ordinal);

        foreach (var file in new[] { FacadeFile }.Concat(Collaborators.Select(name => $"{name}.cs")))
        {
            var source = ReadDownloadSource(file);
            Assert.DoesNotContain("new SqliteConnection", source, StringComparison.Ordinal);
            Assert.DoesNotContain("BeginTransaction", source, StringComparison.Ordinal);
            Assert.DoesNotContain("CommitAsync", source, StringComparison.Ordinal);
            Assert.DoesNotContain("RollbackAsync", source, StringComparison.Ordinal);
            Assert.DoesNotContain("SemaphoreSlim", source, StringComparison.Ordinal);
            Assert.DoesNotContain("DownloadStoreSchema.InitializeAsync", source, StringComparison.Ordinal);
        }
    }

    private static string DownloadSourceRoot => Path.Combine(
        RepositoryRoot,
        "src",
        "DownKyi.Infrastructure",
        "Downloads");

    private static string ReadDownloadSource(string fileName) =>
        File.ReadAllText(Path.Combine(DownloadSourceRoot, fileName));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DownKyi.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
