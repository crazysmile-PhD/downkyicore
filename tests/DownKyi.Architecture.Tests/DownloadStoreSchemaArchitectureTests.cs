using DownKyi.Infrastructure.Downloads;

namespace DownKyi.Architecture.Tests;

public sealed class DownloadStoreSchemaArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void CoordinatorOwnsAtomicFlowWithoutOwningFormatOrSqlRules()
    {
        var source = ReadDownloadSource("DownloadStoreSchema.cs");

        Assert.Contains("public const int CurrentVersion = 10", source, StringComparison.Ordinal);
        Assert.Contains("LegacyDownloadStoreFormatDetector", source, StringComparison.Ordinal);
        Assert.Contains("LegacyDownloadStoreReader", source, StringComparison.Ordinal);
        Assert.Contains("LegacyDownloadStoreNormalizer", source, StringComparison.Ordinal);
        Assert.Contains("CurrentDownloadStoreWriter", source, StringComparison.Ordinal);
        Assert.Contains("BeginTransactionAsync", source, StringComparison.Ordinal);
        Assert.Contains("CommitAsync", source, StringComparison.Ordinal);
        Assert.Contains("RollbackAsync", source, StringComparison.Ordinal);
        Assert.Contains("BackupAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CommandText", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TABLE", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER TABLE", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DetectorOnlyInspectsSchemaFingerprint()
    {
        var source = ReadDownloadSource("LegacyDownloadStoreFormatDetector.cs");

        Assert.Contains("sqlite_master", source, StringComparison.Ordinal);
        Assert.Contains("PRAGMA table_info", source, StringComparison.Ordinal);
        Assert.Contains("LegacyDownloadStoreKind.Relational", source, StringComparison.Ordinal);
        Assert.Contains("LegacyDownloadStoreKind.Stateful", source, StringComparison.Ordinal);
        Assert.Contains("LegacyDownloadStoreKind.Reserved", source, StringComparison.Ordinal);
        Assert.Contains("LegacyDownloadStoreKind.AdmissionSafe", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER TABLE", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE TABLE", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE download", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INSERT INTO", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IPhysicalOutputPathResolver", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ReaderOnlyExtractsLegacyFacts()
    {
        var source = ReadDownloadSource("LegacyDownloadStoreReader.cs");

        Assert.Contains("SELECT db.id", source, StringComparison.Ordinal);
        Assert.Contains("LegacyDownloadStoreSnapshot", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER TABLE", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE TABLE", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE download", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DownloadOutputPathKey", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IPhysicalOutputPathResolver", source, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizerOwnsLegacyMeaningWithoutOwningSql()
    {
        var source = ReadDownloadSource("LegacyDownloadStoreNormalizer.cs");

        Assert.Contains("MapLegacyStatus", source, StringComparison.Ordinal);
        Assert.Contains("DownloadOutputPathKey.Create", source, StringComparison.Ordinal);
        Assert.Contains("IPhysicalOutputPathResolver", source, StringComparison.Ordinal);
        Assert.Contains("Guid.NewGuid", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.Data.Sqlite", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CommandText", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TABLE", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER TABLE", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CurrentWriterOwnsLatestSchemaWithoutVersionBranches()
    {
        var source = ReadDownloadSource("CurrentDownloadStoreWriter.cs");

        Assert.Contains("CREATE TABLE download_base", source, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE download_history", source, StringComparison.Ordinal);
        Assert.Contains("ProjectLegacyHistoryAsync", source, StringComparison.Ordinal);
        Assert.Contains("RecoverCurrentWithLegacyHistoryAsync", source, StringComparison.Ordinal);
        Assert.Contains("ApplyPhaseUpdatesAsync", source, StringComparison.Ordinal);
        Assert.Contains("ApplyReservationUpdatesAsync", source, StringComparison.Ordinal);
        Assert.Contains("ApplyQuarantineAsync", source, StringComparison.Ordinal);
        Assert.Contains("SetUserVersionAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("format.UserVersion", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadStoreSchemaV", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IPhysicalOutputPathResolver", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SET source_table = 'downloading'", source, StringComparison.Ordinal);
    }

    [Fact]
    public void HistoricalVersionMigrationOwnersAreRemoved()
    {
        var owners = typeof(SqliteDownloadTaskStoreOptions)
            .Assembly
            .GetTypes()
            .Where(type => type.Namespace == "DownKyi.Infrastructure.Downloads")
            .Select(type => type.Name)
            .Where(name => name.StartsWith("DownloadStoreSchemaV", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(owners);
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
