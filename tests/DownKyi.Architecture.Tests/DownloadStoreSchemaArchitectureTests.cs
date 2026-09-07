namespace DownKyi.Architecture.Tests;

public sealed class DownloadStoreSchemaArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string[] MigrationOwners =
    [
        "DownloadStoreSchemaV1Migration",
        "DownloadStoreSchemaV2Migration",
        "DownloadStoreSchemaV3Migration"
    ];

    [Fact]
    public void CoordinatorOwnsUpgradeOrderWithoutOwningMigrationSql()
    {
        var source = ReadDownloadSource("DownloadStoreSchema.cs");

        Assert.Contains("public const int CurrentVersion = 3", source, StringComparison.Ordinal);
        Assert.Contains("BeginTransactionAsync", source, StringComparison.Ordinal);
        Assert.Contains("CommitAsync", source, StringComparison.Ordinal);
        Assert.Contains("RollbackAsync", source, StringComparison.Ordinal);
        Assert.Contains("BackupAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CommandText", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TABLE", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER TABLE", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE INDEX", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE downloading", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VersionOwnersAreNamedNonPartialAndReceiveTheCompleteMigrationContext()
    {
        foreach (var owner in MigrationOwners)
        {
            var source = ReadDownloadSource($"{owner}.cs");

            Assert.Contains($"internal static class {owner}", source, StringComparison.Ordinal);
            Assert.DoesNotContain($"partial class {owner}", source, StringComparison.Ordinal);
            Assert.Contains("SqliteConnection connection", source, StringComparison.Ordinal);
            Assert.Contains("SqliteTransaction transaction", source, StringComparison.Ordinal);
            Assert.Contains("DateTimeOffset appliedAtUtc", source, StringComparison.Ordinal);
            Assert.Contains("CancellationToken cancellationToken", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void VersionOwnersDoNotDependOnOneAnother()
    {
        foreach (var owner in MigrationOwners)
        {
            var source = ReadDownloadSource($"{owner}.cs");
            var otherOwners = MigrationOwners.Where(candidate => candidate != owner);

            Assert.All(
                otherOwners,
                otherOwner => Assert.DoesNotContain(otherOwner, source, StringComparison.Ordinal));
        }
    }

    private static string ReadDownloadSource(string fileName)
    {
        return File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "DownKyi.Infrastructure",
            "Downloads",
            fileName));
    }

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
