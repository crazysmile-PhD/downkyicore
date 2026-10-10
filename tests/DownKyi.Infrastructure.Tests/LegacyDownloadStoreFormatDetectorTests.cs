using System.Diagnostics.CodeAnalysis;
using DownKyi.Infrastructure.Downloads;
using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Tests;

public sealed class LegacyDownloadStoreFormatDetectorTests
{
    private static readonly string[] CoreBaseColumns =
    [
        "id", "need_download_content", "bvid", "avid", "cid", "episode_id", "cover_url",
        "page_cover_url", "zone_id", "order", "main_title", "name", "duration",
        "video_codec_name", "resolution", "audio_codec", "file_path", "file_size", "page"
    ];

    private static readonly string[] CoreDownloadingColumns =
    [
        "id", "gid", "download_files", "downloaded_files", "play_stream_type", "download_status",
        "download_content", "download_status_title", "progress", "downloading_file_size", "max_speed",
        "speed_display"
    ];

    private static readonly string[] CoreDownloadedColumns =
    [
        "id", "max_speed_display", "finished_timestamp", "finished_time"
    ];

    private static readonly string[] CurrentHistoryColumns =
    [
        "id", "cid", "zone_id", "order", "main_title", "name", "duration", "video_codec_name",
        "resolution", "audio_codec", "file_size", "published_artifacts", "finished_timestamp",
        "finished_time", "max_speed_display"
    ];

    private static readonly string[] BaseStateColumns =
    [
        "version", "created_at_utc", "updated_at_utc"
    ];

    private static readonly string[] DownloadingStateColumns =
    [
        "phase", "failure_code", "failure_message", "failure_transient", "downloaded_bytes",
        "total_bytes", "bytes_per_second"
    ];

    private static readonly string[] PublishingColumns =
    [
        "publishing_key", "publishing_file_name", "publishing_length", "publishing_sha256"
    ];

    public static TheoryData<string, int, string> RecognizedShapes => new()
    {
        { "empty", 0, "New" },
        { "legacy-v0", 0, "Relational" },
        { "legacy-v1", 1, "Relational" },
        { "legacy-v2", 2, "Stateful" },
        { "legacy-v3", 3, "Reserved" },
        { "legacy-v4", 4, "AdmissionSafe" },
        { "legacy-v5", 5, "AdmissionSafe" },
        { "legacy-v6", 6, "AdmissionSafe" },
        { "legacy-v7", 7, "AdmissionSafe" },
        { "legacy-v8", 8, "AdmissionSafe" },
        { "current-v9-with-legacy-history", 9, "HistoryContentUpgradeWithLegacyHistory" },
        { "current-v9", 9, "HistoryContentUpgrade" },
        { "current-v10-with-legacy-history", 10, "PreviousCurrentWithLegacyHistory" },
        { "current-v10", 10, "PreviousCurrent" },
        { "current-v11-with-legacy-history", 11, "CurrentWithLegacyHistory" },
        { "current-v11", 11, "Current" }
    };

    public static TheoryData<string> MalformedShapes => new()
    {
        "missing-core-table",
        "history-with-legacy-core",
        "partial-state-columns",
        "future-column-on-v4",
        "non-monotonic-v6",
        "partial-publishing-columns",
        "current-with-malformed-legacy-history",
        "current-missing-staging"
    };

    [Theory]
    [MemberData(nameof(RecognizedShapes))]
    public async Task DetectAsyncRecognizesEverySupportedSchemaShape(
        string shape,
        int userVersion,
        string expectedKind)
    {
        ArgumentNullException.ThrowIfNull(shape);
        using var connection = OpenConnection();
        CreateRecognizedShape(connection, shape, userVersion);

        var result = await LegacyDownloadStoreFormatDetector.DetectAsync(
            connection,
            databaseExisted: true,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(expectedKind, result.Kind.ToString());
    }

    [Theory]
    [MemberData(nameof(MalformedShapes))]
    public async Task DetectAsyncRejectsMalformedSchemaShapes(string shape)
    {
        using var connection = OpenConnection();
        CreateMalformedShape(connection, shape);

        var result = await LegacyDownloadStoreFormatDetector.DetectAsync(
            connection,
            databaseExisted: true,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(LegacyDownloadStoreKind.Unsupported, result.Kind);
    }

    [Fact]
    public async Task CurrentSchemaMismatchNamesOnlyKnownMissingFields()
    {
        using var connection = OpenConnection();
        SetUserVersion(connection, DownloadStoreSchema.CurrentVersion);
        CreateCurrentShape(connection, includeStagingToken: false);
        CreateTable(connection, "private_custom_table_73918", ["private_column_73918"]);

        var format = await LegacyDownloadStoreFormatDetector.DetectAsync(
            connection,
            databaseExisted: true,
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        var failure = await Assert.ThrowsAsync<DownloadStoreSchemaMismatchException>(() =>
            LegacyDownloadStoreReader.ReadAsync(
                connection,
                transaction,
                format,
                TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Equal(DownloadStoreSchema.CurrentVersion, failure.UserVersion);
        Assert.Contains("missing column: download_base.staging_token", failure.SchemaDifferences);
        Assert.Contains("download_base.staging_token", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private_custom_table_73918", failure.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain("private_column_73918", failure.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("future-column-on-v4",
        "unexpected column for declared version: download_base.nfo_request")]
    [InlineData("partial-state-columns", "missing column: download_base.created_at_utc")]
    [InlineData("partial-publishing-columns", "missing column: download_base.publishing_file_name")]
    public async Task LegacySchemaMismatchIdentifiesVersionedFeatureDifference(
        string shape,
        string expectedDifference)
    {
        using var connection = OpenConnection();
        CreateMalformedShape(connection, shape);

        var format = await LegacyDownloadStoreFormatDetector.DetectAsync(
            connection,
            databaseExisted: true,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(LegacyDownloadStoreKind.Unsupported, format.Kind);
        Assert.Contains(expectedDifference, format.SchemaDifferences);
    }

    [Fact]
    public async Task DetectAsyncRejectsANewerSchemaVersionBeforeClassifyingItsShape()
    {
        using var connection = OpenConnection();
        SetUserVersion(connection, DownloadStoreSchema.CurrentVersion + 1);

        var exception = await Assert.ThrowsAsync<DownloadStoreSchemaMismatchException>(() =>
            LegacyDownloadStoreFormatDetector.DetectAsync(
                connection,
                databaseExisted: true,
                TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Equal(DownloadStoreSchema.CurrentVersion + 1, exception.UserVersion);
        Assert.Contains("newer than supported schema", exception.Message, StringComparison.Ordinal);
    }

    private static SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        return connection;
    }

    private static void CreateRecognizedShape(
        SqliteConnection connection,
        string shape,
        int userVersion)
    {
        SetUserVersion(connection, userVersion);
        if (shape == "empty")
        {
            return;
        }

        if (shape == "current-v9")
        {
            CreateCurrentShape(connection, includeStagingToken: true, includeContentColumn: false);
            return;
        }

        if (shape == "current-v9-with-legacy-history")
        {
            CreateCurrentShape(connection, includeStagingToken: true, includeContentColumn: false);
            CreateTable(connection, "downloaded", CoreDownloadedColumns);
            return;
        }

        if (shape is "current-v10" or "current-v10-with-legacy-history"
            or "current-v11" or "current-v11-with-legacy-history")
        {
            CreateCurrentShape(connection, includeStagingToken: true);
            if (shape.EndsWith("with-legacy-history", StringComparison.Ordinal))
            {
                CreateTable(connection, "downloaded", CoreDownloadedColumns);
            }

            return;
        }

        CreateLegacyShape(connection, userVersion);
    }

    private static void CreateMalformedShape(SqliteConnection connection, string shape)
    {
        switch (shape)
        {
            case "missing-core-table":
                SetUserVersion(connection, 0);
                CreateTable(connection, "download_base", CoreBaseColumns);
                CreateTable(connection, "downloading", CoreDownloadingColumns);
                break;
            case "history-with-legacy-core":
                CreateLegacyShape(connection, 1);
                CreateTable(connection, "download_history", ["id"]);
                break;
            case "partial-state-columns":
                CreateLegacyShape(connection, 1);
                AddColumn(connection, "download_base", "version");
                SetUserVersion(connection, 2);
                break;
            case "future-column-on-v4":
                CreateLegacyShape(connection, 4);
                AddColumn(connection, "download_base", "nfo_request");
                break;
            case "non-monotonic-v6":
                CreateLegacyShape(connection, 4);
                AddColumn(connection, "download_base", "published_artifacts");
                SetUserVersion(connection, 6);
                break;
            case "partial-publishing-columns":
                CreateLegacyShape(connection, 7);
                AddColumn(connection, "download_base", "publishing_key");
                SetUserVersion(connection, 8);
                break;
            case "current-with-malformed-legacy-history":
                SetUserVersion(connection, DownloadStoreSchema.CurrentVersion);
                CreateCurrentShape(connection, includeStagingToken: true);
                CreateTable(connection, "downloaded", ["id"]);
                break;
            case "current-missing-staging":
                SetUserVersion(connection, DownloadStoreSchema.CurrentVersion);
                CreateCurrentShape(connection, includeStagingToken: false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown malformed schema fixture.");
        }
    }

    private static void CreateLegacyShape(SqliteConnection connection, int version)
    {
        var baseColumns = new List<string>(CoreBaseColumns);
        var downloadingColumns = new List<string>(CoreDownloadingColumns);
        if (version >= 2)
        {
            baseColumns.AddRange(BaseStateColumns);
            downloadingColumns.AddRange(DownloadingStateColumns);
        }

        if (version >= 3)
        {
            baseColumns.Add("output_reservation_key");
        }

        if (version >= 5)
        {
            baseColumns.Add("nfo_request");
        }

        if (version >= 6)
        {
            baseColumns.Add("published_artifacts");
        }

        if (version >= 7)
        {
            baseColumns.Add("staging_token");
        }

        if (version >= 8)
        {
            baseColumns.AddRange(PublishingColumns);
        }

        CreateTable(connection, "download_base", baseColumns);
        CreateTable(connection, "downloading", downloadingColumns);
        CreateTable(connection, "downloaded", CoreDownloadedColumns);
        if (version >= 1)
        {
            CreateMarkerTable(connection, "download_schema_migrations");
            CreateMarkerTable(connection, "download_quarantine");
        }

        if (version >= 4)
        {
            CreateMarkerTable(connection, "download_upgrade_admission_gate");
        }

        SetUserVersion(connection, version);
    }

    private static void CreateCurrentShape(
        SqliteConnection connection,
        bool includeStagingToken,
        bool includeContentColumn = true)
    {
        var baseColumns = new List<string>(CoreBaseColumns);
        baseColumns.AddRange(BaseStateColumns);
        baseColumns.Add("output_reservation_key");
        baseColumns.Add("nfo_request");
        baseColumns.Add("published_artifacts");
        if (includeStagingToken)
        {
            baseColumns.Add("staging_token");
        }

        baseColumns.AddRange(PublishingColumns);
        CreateTable(connection, "download_base", baseColumns);
        CreateTable(
            connection,
            "downloading",
            [.. CoreDownloadingColumns, .. DownloadingStateColumns]);
        CreateTable(connection, "download_history", includeContentColumn
            ? [.. CurrentHistoryColumns, "requested_content"]
            : CurrentHistoryColumns);
        CreateMarkerTable(connection, "download_schema_migrations");
        CreateMarkerTable(connection, "download_quarantine");
        CreateMarkerTable(connection, "download_upgrade_admission_gate");
    }

    private static void CreateMarkerTable(SqliteConnection connection, string table) =>
        Execute(connection, $"CREATE TABLE [{table}] ([id] INTEGER)");

    private static void CreateTable(
        SqliteConnection connection,
        string table,
        IEnumerable<string> columns) =>
        Execute(
            connection,
            $"CREATE TABLE [{table}] ({string.Join(", ", columns.Select(column => $"[{column}] TEXT"))})");

    private static void AddColumn(SqliteConnection connection, string table, string column) =>
        Execute(connection, $"ALTER TABLE [{table}] ADD COLUMN [{column}] TEXT");

    private static void SetUserVersion(SqliteConnection connection, int version) =>
        Execute(connection, $"PRAGMA user_version = {version}");

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "All SQL identifiers and fragments are fixed test schema fixtures.")]
    private static void Execute(SqliteConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }
}

