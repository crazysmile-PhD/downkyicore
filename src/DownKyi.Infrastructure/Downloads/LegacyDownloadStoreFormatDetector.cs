using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Downloads;

internal static class LegacyDownloadStoreFormatDetector
{
    private static readonly string[] CoreBaseColumns =
    [
        "id",
        "need_download_content",
        "bvid",
        "avid",
        "cid",
        "episode_id",
        "cover_url",
        "page_cover_url",
        "zone_id",
        "order",
        "main_title",
        "name",
        "duration",
        "video_codec_name",
        "resolution",
        "audio_codec",
        "file_path",
        "file_size",
        "page"
    ];

    private static readonly string[] CoreDownloadingColumns =
    [
        "id",
        "gid",
        "download_files",
        "downloaded_files",
        "play_stream_type",
        "download_status",
        "download_content",
        "download_status_title",
        "progress",
        "downloading_file_size",
        "max_speed",
        "speed_display"
    ];

    private static readonly string[] CoreDownloadedColumns =
    [
        "id",
        "max_speed_display",
        "finished_timestamp",
        "finished_time"
    ];

    private static readonly string[] CurrentHistoryColumns =
    [
        "id",
        "cid",
        "zone_id",
        "order",
        "main_title",
        "name",
        "duration",
        "video_codec_name",
        "resolution",
        "audio_codec",
        "file_size",
        "published_artifacts",
        "finished_timestamp",
        "finished_time",
        "max_speed_display"
    ];

    private static readonly string[] BaseStateColumns =
    [
        "version",
        "created_at_utc",
        "updated_at_utc"
    ];

    private static readonly string[] DownloadingStateColumns =
    [
        "phase",
        "failure_code",
        "failure_message",
        "failure_transient",
        "downloaded_bytes",
        "total_bytes",
        "bytes_per_second"
    ];

    private static readonly string[] PublishingColumns =
    [
        "publishing_key",
        "publishing_file_name",
        "publishing_length",
        "publishing_sha256"
    ];

    public static async Task<LegacyDownloadStoreFormat> DetectAsync(
        SqliteConnection connection,
        bool databaseExisted,
        CancellationToken cancellationToken)
    {
        if (!databaseExisted)
        {
            return new LegacyDownloadStoreFormat(
                Kind: LegacyDownloadStoreKind.New,
                DatabaseExisted: false,
                UserVersion: 0,
                HasCoreTables: false,
                HasSchemaLedger: false,
                HasQuarantine: false,
                HasStateColumns: false,
                HasReservationKey: false,
                HasAdmissionGate: false,
                HasNfoRequest: false,
                HasPublishedArtifacts: false,
                HasStagingToken: false,
                HasPublishingArtifact: false);
        }

        var userVersion = await DownloadStoreSchemaLifecycle
            .ReadUserVersionAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        if (userVersion > DownloadStoreSchema.CurrentVersion)
        {
            throw new InvalidOperationException(
                $"Download database schema {userVersion} is newer than supported schema {DownloadStoreSchema.CurrentVersion}.");
        }

        var tables = await ReadTableNamesAsync(connection, cancellationToken).ConfigureAwait(false);
        var baseColumns = tables.Contains("download_base")
            ? await ReadDownloadBaseColumnsAsync(connection, cancellationToken).ConfigureAwait(false)
            : [];
        var downloadingColumns = tables.Contains("downloading")
            ? await ReadDownloadingColumnsAsync(connection, cancellationToken).ConfigureAwait(false)
            : [];
        var downloadedColumns = tables.Contains("downloaded")
            ? await ReadDownloadedColumnsAsync(connection, cancellationToken).ConfigureAwait(false)
            : [];
        var historyColumns = tables.Contains("download_history")
            ? await ReadHistoryColumnsAsync(connection, cancellationToken).ConfigureAwait(false)
            : [];
        var hasLegacyCoreTables = CoreBaseColumns.All(baseColumns.Contains)
                                  && CoreDownloadingColumns.All(downloadingColumns.Contains)
                                  && CoreDownloadedColumns.All(downloadedColumns.Contains);
        var hasCurrentHistoryShape = CoreBaseColumns.All(baseColumns.Contains)
                                     && CoreDownloadingColumns.All(downloadingColumns.Contains)
                                     && CurrentHistoryColumns.All(historyColumns.Contains)
                                     && !tables.Contains("downloaded");
        var hasStateColumns = BaseStateColumns.All(baseColumns.Contains)
                              && DownloadingStateColumns.All(downloadingColumns.Contains);
        var hasAnyStateColumns = BaseStateColumns.Any(baseColumns.Contains)
                                 || DownloadingStateColumns.Any(downloadingColumns.Contains);
        var hasReservationKey = baseColumns.Contains("output_reservation_key");
        var hasAdmissionGate = tables.Contains("download_upgrade_admission_gate");
        var hasNfoRequest = baseColumns.Contains("nfo_request");
        var hasPublishedArtifacts = baseColumns.Contains("published_artifacts");
        var hasStagingToken = baseColumns.Contains("staging_token");
        var hasPublishingArtifact = PublishingColumns.All(baseColumns.Contains);
        var hasAnyPublishingArtifact = PublishingColumns.Any(baseColumns.Contains);
        var fingerprint = new DownloadStoreSchemaFingerprint(
            UserVersion: userVersion,
            HasNoTables: tables.Count == 0,
            HasLegacyCoreTables: hasLegacyCoreTables,
            HasHistoryTable: tables.Contains("download_history"),
            HasCurrentHistoryShape: hasCurrentHistoryShape,
            HasSchemaLedger: tables.Contains("download_schema_migrations"),
            HasQuarantine: tables.Contains("download_quarantine"),
            HasStateColumns: hasStateColumns,
            HasAnyStateColumns: hasAnyStateColumns,
            HasReservationKey: hasReservationKey,
            HasAdmissionGate: hasAdmissionGate,
            HasNfoRequest: hasNfoRequest,
            HasPublishedArtifacts: hasPublishedArtifacts,
            HasStagingToken: hasStagingToken,
            HasPublishingArtifact: hasPublishingArtifact,
            HasAnyPublishingArtifact: hasAnyPublishingArtifact);
        var kind = DetectKind(fingerprint);
        return new LegacyDownloadStoreFormat(
            Kind: kind,
            DatabaseExisted: true,
            UserVersion: userVersion,
            HasCoreTables: hasLegacyCoreTables || hasCurrentHistoryShape,
            HasSchemaLedger: tables.Contains("download_schema_migrations"),
            HasQuarantine: tables.Contains("download_quarantine"),
            HasStateColumns: hasStateColumns,
            HasReservationKey: hasReservationKey,
            HasAdmissionGate: hasAdmissionGate,
            HasNfoRequest: hasNfoRequest,
            HasPublishedArtifacts: hasPublishedArtifacts,
            HasStagingToken: hasStagingToken,
            HasPublishingArtifact: hasPublishingArtifact);
    }

    private static LegacyDownloadStoreKind DetectKind(DownloadStoreSchemaFingerprint fingerprint)
    {
        if (fingerprint.IsNew)
        {
            return LegacyDownloadStoreKind.New;
        }

        if (fingerprint.IsCurrent)
        {
            return LegacyDownloadStoreKind.Current;
        }

        if (fingerprint.IsStructurallyIncomplete)
        {
            return LegacyDownloadStoreKind.Unsupported;
        }

        if (!fingerprint.HasStateColumns)
        {
            return fingerprint.IsRelational
                ? LegacyDownloadStoreKind.Relational
                : LegacyDownloadStoreKind.Unsupported;
        }

        if (!fingerprint.HasRequiredSchemaMetadata)
        {
            return LegacyDownloadStoreKind.Unsupported;
        }

        if (!fingerprint.HasReservationKey)
        {
            return fingerprint.IsStateful
                ? LegacyDownloadStoreKind.Stateful
                : LegacyDownloadStoreKind.Unsupported;
        }

        if (!fingerprint.HasAdmissionGate)
        {
            return fingerprint.IsReserved
                ? LegacyDownloadStoreKind.Reserved
                : LegacyDownloadStoreKind.Unsupported;
        }

        return fingerprint.IsAdmissionSafe
            ? LegacyDownloadStoreKind.AdmissionSafe
            : LegacyDownloadStoreKind.Unsupported;
    }

    private readonly record struct DownloadStoreSchemaFingerprint(
        int UserVersion,
        bool HasNoTables,
        bool HasLegacyCoreTables,
        bool HasHistoryTable,
        bool HasCurrentHistoryShape,
        bool HasSchemaLedger,
        bool HasQuarantine,
        bool HasStateColumns,
        bool HasAnyStateColumns,
        bool HasReservationKey,
        bool HasAdmissionGate,
        bool HasNfoRequest,
        bool HasPublishedArtifacts,
        bool HasStagingToken,
        bool HasPublishingArtifact,
        bool HasAnyPublishingArtifact)
    {
        public bool IsNew => UserVersion == 0 && HasNoTables;

        public bool IsCurrent => HasCurrentHistoryShape
                                 && UserVersion == DownloadStoreSchema.CurrentVersion
                                 && HasRequiredSchemaMetadata
                                 && HasStateColumns
                                 && HasReservationKey
                                 && HasAdmissionGate
                                 && HasNfoRequest
                                 && HasPublishedArtifacts
                                 && HasStagingToken
                                 && HasPublishingArtifact;

        public bool IsStructurallyIncomplete => HasHistoryTable
                                                || !HasLegacyCoreTables
                                                || HasAnyStateColumns != HasStateColumns
                                                || HasAnyPublishingArtifact != HasPublishingArtifact;

        public bool HasRequiredSchemaMetadata => HasSchemaLedger && HasQuarantine;

        public bool IsRelational => HasOnlyRelationalColumns && (IsKnownV0 || IsKnownV1);

        public bool IsStateful => UserVersion == 2
                                  && !HasAdmissionGate
                                  && !HasNfoRequest
                                  && !HasPublishedArtifacts
                                  && !HasStagingToken
                                  && !HasPublishingArtifact;

        public bool IsReserved => UserVersion == 3
                                  && !HasNfoRequest
                                  && !HasPublishedArtifacts
                                  && !HasStagingToken
                                  && !HasPublishingArtifact;

        public bool IsAdmissionSafe => HasMonotonicAdditiveShape
                                       && UserVersion == AdditiveShapeVersion;

        private bool HasOnlyRelationalColumns => !HasReservationKey
                                                 && !HasAdmissionGate
                                                 && !HasNfoRequest
                                                 && !HasPublishedArtifacts
                                                 && !HasStagingToken
                                                 && !HasPublishingArtifact;

        private bool IsKnownV0 => UserVersion == 0 && !HasSchemaLedger && !HasQuarantine;

        private bool IsKnownV1 => UserVersion == 1 && HasRequiredSchemaMetadata;

        private int AdditiveShapeVersion => HasPublishingArtifact
            ? 8
            : HasStagingToken
                ? 7
                : HasPublishedArtifacts
                    ? 6
                    : HasNfoRequest
                        ? 5
                        : 4;

        private bool HasMonotonicAdditiveShape => (!HasPublishedArtifacts || HasNfoRequest)
                                                  && (!HasStagingToken || HasPublishedArtifacts)
                                                  && (!HasPublishingArtifact || HasStagingToken);
    }

    private static async Task<HashSet<string>> ReadDownloadBaseColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(download_base)";
        return await ReadColumnNamesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<HashSet<string>> ReadDownloadingColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(downloading)";
        return await ReadColumnNamesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<HashSet<string>> ReadDownloadedColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(downloaded)";
        return await ReadColumnNamesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<HashSet<string>> ReadHistoryColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(download_history)";
        return await ReadColumnNamesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<HashSet<string>> ReadColumnNamesAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static async Task<HashSet<string>> ReadTableNamesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
