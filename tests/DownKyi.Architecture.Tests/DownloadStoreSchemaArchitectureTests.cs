using DownKyi.Infrastructure.Downloads;

namespace DownKyi.Architecture.Tests;

public sealed class DownloadStoreSchemaArchitectureTests
{
    private const string MigrationOwnerNamespace = "DownKyi.Infrastructure.Downloads";
    private const string MigrationOwnerPrefix = "DownloadStoreSchemaV";
    private const string MigrationOwnerSuffix = "Migration";
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string[] MigrationOwners = typeof(SqliteDownloadTaskStoreOptions)
        .Assembly
        .GetTypes()
        .Where(IsMigrationOwner)
        .Select(type => type.Name)
        .Order(StringComparer.Ordinal)
        .ToArray();

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
    public void VersionOwnerDiscoveryIncludesFutureVersionsAndRejectsLookalikes()
    {
        Assert.NotEmpty(MigrationOwners);
        Assert.True(IsMigrationOwner(MigrationOwnerNamespace, "DownloadStoreSchemaV4Migration"));
        Assert.False(IsMigrationOwner(MigrationOwnerNamespace, "DownloadStoreSchemaV0Migration"));
        Assert.False(IsMigrationOwner(MigrationOwnerNamespace, "DownloadStoreSchemaV04Migration"));
        Assert.False(IsMigrationOwner(MigrationOwnerNamespace, "DownloadStoreSchemaV4MigrationHelper"));
        Assert.False(IsMigrationOwner("DownKyi.Infrastructure.Other", "DownloadStoreSchemaV4Migration"));
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

    private static bool IsMigrationOwner(Type type)
    {
        return type.DeclaringType is null && IsMigrationOwner(type.Namespace, type.Name);
    }

    private static bool IsMigrationOwner(string? typeNamespace, string typeName)
    {
        if (!string.Equals(typeNamespace, MigrationOwnerNamespace, StringComparison.Ordinal)
            || !typeName.StartsWith(MigrationOwnerPrefix, StringComparison.Ordinal)
            || !typeName.EndsWith(MigrationOwnerSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        var version = typeName.AsSpan(
            MigrationOwnerPrefix.Length,
            typeName.Length - MigrationOwnerPrefix.Length - MigrationOwnerSuffix.Length);
        if (version.IsEmpty || version[0] == '0')
        {
            return false;
        }

        foreach (var character in version)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
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
