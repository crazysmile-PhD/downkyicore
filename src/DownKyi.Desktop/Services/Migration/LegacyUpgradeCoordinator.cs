using System;
using System.Collections.Generic;
using System.Formats.Nrbf;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Diagnostics;
using DownKyi.Application.Downloads;
using DownKyi.Application.Time;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Domain.Downloads;
using DownKyi.Infrastructure.Bilibili;
using DownKyi.Infrastructure.Storage;
using DownKyi.Infrastructure.Storage.Database;
using DownKyi.Models;
using DownKyi.Utils;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Migration;

internal enum LegacyUpgradeOutcome
{
    NoMigration,
    Completed,
    Failed
}

internal sealed record LegacyUpgradeProgress(string Message, double? Percent = null);

internal sealed record LegacyUpgradeResult(
    LegacyUpgradeOutcome Outcome,
    string? ErrorMessage = null,
    Exception? Failure = null);

internal interface ILegacyUpgradeCoordinator
{
    bool IsUpgradeRequired();

    Task<LegacyUpgradeResult> UpgradeAsync(
        IProgress<LegacyUpgradeProgress> progress,
        CancellationToken cancellationToken);
}

internal sealed class LegacyUpgradeCoordinator : ILegacyUpgradeCoordinator
{
    private const int BatchSize = 200;
    private readonly IDownloadHistoryService _history;
    private readonly IClock _clock;
    private readonly ILogger<LegacyUpgradeCoordinator> _logger;

    public LegacyUpgradeCoordinator(
        IDownloadHistoryService history,
        IClock clock,
        ILogger<LegacyUpgradeCoordinator> logger)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsUpgradeRequired()
    {
        try
        {
            var loginPath = ApplicationStorage.GetLogin();
            if (File.Exists(loginPath))
            {
                using Stream stream = File.Open(
                    loginPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);
                if (NrbfDecoder.StartsWithPayloadHeader(stream))
                {
                    return true;
                }
            }

            return FindLegacyDatabase() != null;
        }
        catch (Exception exception) when (IsMigrationException(exception))
        {
            _logger.LogErrorMessage(
                "Legacy upgrade preflight failed; preserving the diagnostic migration path.",
                exception);
            return true;
        }
    }

    public Task<LegacyUpgradeResult> UpgradeAsync(
        IProgress<LegacyUpgradeProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return Task.Run(() => UpgradeCoreAsync(progress, cancellationToken), cancellationToken);
    }

    private async Task<LegacyUpgradeResult> UpgradeCoreAsync(
        IProgress<LegacyUpgradeProgress> progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TryMigrateLogin(progress, cancellationToken);

        var oldDatabasePath = FindLegacyDatabase();
        if (oldDatabasePath == null)
        {
            return new LegacyUpgradeResult(LegacyUpgradeOutcome.NoMigration);
        }

        return await UpgradeDatabaseAsync(oldDatabasePath, progress, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<LegacyUpgradeResult> UpgradeDatabaseAsync(
        string databasePath,
        IProgress<LegacyUpgradeProgress> progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress.Report(new LegacyUpgradeProgress(DictionaryResource.GetString("MigrationDownloading", "正在迁移下载信息")));
        string? backupPath = null;
        try
        {
            {
                using var database = OpenLegacyDatabase(databasePath, progress, cancellationToken);
                if (!HasLegacyDownloads(database))
                {
                    return new LegacyUpgradeResult(LegacyUpgradeOutcome.NoMigration);
                }

                var records = ReadLegacyRecords(database, cancellationToken);
                await PersistRecordsAsync(records, progress, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                backupPath = BackupMigratedDatabase(database, databasePath);
            }

            cancellationToken.ThrowIfCancellationRequested();
            // History and a complete SQLite snapshot are committed, and source
            // handles are closed before retiring the legacy file set.
            File.Delete(databasePath);
            File.Delete(databasePath + "-wal");
            File.Delete(databasePath + "-shm");
            File.Delete(databasePath + "-journal");

            progress.Report(new LegacyUpgradeProgress(DictionaryResource.GetString("MigrationDownloadComplete", "下载信息迁移完成"), 100));
            return new LegacyUpgradeResult(LegacyUpgradeOutcome.Completed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (IsMigrationException(e))
        {
            _logger.LogErrorMessage("Legacy upgrade failed.", e);
            var preservationMessage = backupPath == null
                ? DictionaryResource.GetString("MigrationSourcePreserved", "旧数据库及 WAL 已保留在原位置，请保留日志并手动处理")
                : DictionaryResource.Format("MigrationBackupCreated", "原数据库已备份为 {0}", Path.GetFileName(backupPath));
            var message = DictionaryResource.Format(
                "MigrationFailedWithBackup", "数据迁移失败: {0}; {1}", e.Message, preservationMessage);
            return new LegacyUpgradeResult(
                LegacyUpgradeOutcome.Failed,
                message,
                e);
        }
    }

    private void TryMigrateLogin(
        IProgress<LegacyUpgradeProgress> progress,
        CancellationToken cancellationToken)
    {
        try
        {
            MigrateLogin(progress, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (IsLegacyRecordException(e) || e is IOException or UnauthorizedAccessException)
        {
            _logger.LogErrorMessage("Legacy login migration failed; download migration will continue.", e);
            progress.Report(new LegacyUpgradeProgress(DictionaryResource.GetString("MigrationLoginFailedContinuing", "登录信息迁移失败，继续迁移下载信息")));
        }
    }

    private static void MigrateLogin(
        IProgress<LegacyUpgradeProgress> progress,
        CancellationToken cancellationToken)
    {
        var loginPath = ApplicationStorage.GetLogin();
        if (!File.Exists(loginPath))
        {
            return;
        }

        using Stream stream = File.Open(loginPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!NrbfDecoder.StartsWithPayloadHeader(stream))
        {
            return;
        }

        progress.Report(new LegacyUpgradeProgress(DictionaryResource.GetString("MigrationLoginInProgress", "正在迁移登录信息")));
        var cookies = ReadLegacyCookies(stream, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!LoginHelper.SaveLoginInfoCookies(cookies))
        {
            throw new IOException("Legacy login cookies could not be persisted.");
        }
        progress.Report(new LegacyUpgradeProgress(DictionaryResource.GetString("MigrationLoginComplete", "登录信息迁移完成")));
    }

    internal static IReadOnlyList<DownKyiCookie> ReadLegacyCookies(
        Stream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var cookies = new List<DownKyiCookie>();
        var cookieRecord = NrbfDecoder.DecodeClassRecord(stream);
        if (cookieRecord.TypeNameMatches(typeof(CookieContainer)))
        {
            var values = cookieRecord
                .GetClassRecord("m_domainTable")?
                .GetArrayRecord("Values")?
                .GetArray(typeof(object[]));
            foreach (var value in EnumerateClassRecords(
                         values,
                         "CookieContainer.m_domainTable.Values"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var valueObjects = value
                    .GetClassRecord("m_list")?
                    .GetClassRecord("_list")?
                    .GetArrayRecord("values")?
                    .GetArray(typeof(object[]));
                foreach (var valueObject in EnumerateClassRecords(
                             valueObjects,
                             "PathList.m_list._list.values"))
                {
                    var records = valueObject
                        .GetClassRecord("m_list")?
                        .GetArrayRecord("_items")?
                        .GetArray(typeof(object[]));
                    foreach (var cookie in EnumerateClassRecords(
                                 records,
                                 "CookieCollection.m_list._items"))
                    {
                        cookies.Add(new DownKyiCookie(
                            cookie.GetString("m_name") ?? string.Empty,
                            cookie.GetString("m_value") ?? string.Empty,
                            cookie.GetString("m_domain")));
                    }
                }
            }
        }

        return cookies;
    }

    internal static IEnumerable<ClassRecord> EnumerateClassRecords(
        Array? records,
        string source)
    {
        if (records == null)
        {
            yield break;
        }

        foreach (var record in records)
        {
            if (record == null)
            {
                continue;
            }

            if (record is not ClassRecord classRecord)
            {
                throw new InvalidDataException(
                    $"Legacy NRBF array '{source}' contained an unexpected "
                    + $"'{record.GetType().FullName}' record.");
            }

            yield return classRecord;
        }
    }

    private static string? FindLegacyDatabase()
    {
        var downloadPath = ApplicationStorage.GetDownload();
        var possiblePaths = new[]
        {
            downloadPath,
            downloadPath.Replace(".db", "_debug.db", StringComparison.Ordinal)
        };
        return possiblePaths.FirstOrDefault(File.Exists);
    }

    // The passphrase is part of the published legacy file format. Keep one
    // authoritative value for the importer and its compatibility fixtures.
    internal static string GetLegacyDatabasePassword()
    {
        return "bdb8eb69-3698-4af9-b722-9312d0fba623";
    }

    private static SqliteDatabase OpenLegacyDatabase(
        string databasePath,
        IProgress<LegacyUpgradeProgress> progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return new SqliteDatabase(databasePath, GetLegacyDatabasePassword());
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 26)
        {
            // Only an unreadable format warrants the legacy plaintext fallback.
            // Once opened, schema/record/persistence errors retain their cause.
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new LegacyUpgradeProgress(DictionaryResource.GetString("MigrationTryingAlternateConnection", "尝试备用连接方式")));
            return new SqliteDatabase(databasePath);
        }
    }

    private static bool HasLegacyDownloads(SqliteDatabase database)
    {
        var tableCount = 0;
        var hasDownloadedTable = false;
        database.ExecuteQuery(
            command => command.CommandText = """
                SELECT name
                FROM sqlite_master
                WHERE type='table' AND name NOT LIKE 'sqlite_%'
                """,
            reader =>
            {
                while (reader.Read())
                {
                    tableCount++;
                    hasDownloadedTable |= string.Equals(reader.GetString(0), "downloaded", StringComparison.OrdinalIgnoreCase);
                }
            });
        if (tableCount == 0)
        {
            return false;
        }

        if (!hasDownloadedTable)
        {
            throw new InvalidDataException("不支持的旧下载数据库结构: 缺少 downloaded 表");
        }

        // Old versions created download_base only when a download was used.
        // An empty completed-history table needs no detail table or conversion.
        var hasDownloads = false;
        database.ExecuteQuery(
            command => command.CommandText = "SELECT EXISTS(SELECT 1 FROM downloaded)",
            reader => hasDownloads = reader.Read() && reader.GetBoolean(0));
        return hasDownloads;
    }

    private static Dictionary<string, DownloadedWithData> ReadLegacyRecords(
        SqliteDatabase database,
        CancellationToken cancellationToken)
    {
        var records = new Dictionary<string, DownloadedWithData>(StringComparer.Ordinal);
        database.ExecuteQuery(
            command => command.CommandText = """
                SELECT d.id, d.data AS downloaded_data, db.data AS download_base_data
                FROM downloaded d
                LEFT JOIN download_base db ON d.id = db.id
                """,
            reader =>
            {
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var stream = new MemoryStream((byte[])reader["downloaded_data"]);
                    var record = NrbfDecoder.DecodeClassRecord(stream);
                    if (!record.TypeNameMatches(typeof(Downloaded)))
                    {
                        throw new InvalidDataException("不支持的旧下载记录格式");
                    }

                    var id = reader.GetString(reader.GetOrdinal("id"));
                    records[id] = new DownloadedWithData(
                        new Downloaded
                        {
                            Id = id,
                            MaxSpeedDisplay = record.GetString($"<{nameof(Downloaded.MaxSpeedDisplay)}>k__BackingField"),
                            FinishedTime = record.GetString($"<{nameof(Downloaded.FinishedTime)}>k__BackingField") ?? string.Empty,
                            FinishedTimestamp = record.GetInt64($"<{nameof(Downloaded.FinishedTimestamp)}>k__BackingField")
                        },
                        (byte[])reader["download_base_data"]);
                }
            });
        return records;
    }

    private async Task PersistRecordsAsync(
        Dictionary<string, DownloadedWithData> records,
        IProgress<LegacyUpgradeProgress> progress,
        CancellationToken cancellationToken)
    {
        var batch = new List<DownloadHistoryRecord>(Math.Min(BatchSize, records.Count));
        var visited = 0;
        foreach (var item in records.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            visited++;
            var downloaded = ConvertDownload(item);
            batch.Add(DownloadHistoryRecord.FromCompletedTask(
                LegacyDownloadHistoryTaskFactory.Create(downloaded, _clock.UtcNow)));

            if (batch.Count >= BatchSize)
            {
                await PersistBatchAsync(batch, cancellationToken).ConfigureAwait(false);
                batch.Clear();
            }

            var percent = records.Count == 0 ? 100 : visited / (double)records.Count * 100;
            progress.Report(new LegacyUpgradeProgress(
                DictionaryResource.Format("MigrationDownloadProgress", "正在迁移下载信息({0}/{1})", visited, records.Count),
                percent));
        }

        if (batch.Count > 0)
        {
            await PersistBatchAsync(batch, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task PersistBatchAsync(
        IEnumerable<DownloadHistoryRecord> batch,
        CancellationToken cancellationToken)
    {
        foreach (var history in batch)
        {
            var result = await _history.AddAsync(history, cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                throw new InvalidOperationException(result.Error?.Message ?? "Download storage operation failed.");
            }
        }
    }

    private static Downloaded ConvertDownload(DownloadedWithData item)
    {
        using var stream = new MemoryStream(item.DownloadBaseData);
        var record = NrbfDecoder.DecodeClassRecord(stream);
        if (!record.TypeNameMatches(typeof(DownloadBase)))
        {
            throw new InvalidDataException("不支持的旧下载详情格式");
        }

        var needDownloadContentRecord = record.GetClassRecord(
            $"<{nameof(DownloadBase.NeedDownloadContent)}>k__BackingField");
        return new Downloaded
        {
            Id = item.Downloaded.Id,
            MaxSpeedDisplay = item.Downloaded.MaxSpeedDisplay,
            FinishedTime = item.Downloaded.FinishedTime,
            FinishedTimestamp = item.Downloaded.FinishedTimestamp,
            DownloadBase = new DownloadBase
            {
                Id = item.Downloaded.Id,
                NeedDownloadContent = needDownloadContentRecord == null
                    ? DownloadContentSelection.None
                    : DownloadContentSelection.FromLegacyMap(
                        LegacyDownloadTaskMapper.ReadRequestedAssets(needDownloadContentRecord)),
                Bvid = ReadString(record, nameof(DownloadBase.Bvid)),
                Avid = record.GetInt64($"<{nameof(DownloadBase.Avid)}>k__BackingField"),
                Cid = record.GetInt64($"<{nameof(DownloadBase.Cid)}>k__BackingField"),
                EpisodeId = record.GetInt64($"<{nameof(DownloadBase.EpisodeId)}>k__BackingField"),
                CoverUrl = ReadString(record, nameof(DownloadBase.CoverUrl)),
                PageCoverUrl = ReadString(record, nameof(DownloadBase.PageCoverUrl)),
                ZoneId = record.GetInt32($"<{nameof(DownloadBase.ZoneId)}>k__BackingField"),
                Order = record.GetInt32($"<{nameof(DownloadBase.Order)}>k__BackingField"),
                MainTitle = ReadString(record, nameof(DownloadBase.MainTitle)),
                Name = ReadString(record, nameof(DownloadBase.Name)),
                Duration = ReadString(record, nameof(DownloadBase.Duration)),
                VideoCodecName = ReadString(record, nameof(DownloadBase.VideoCodecName)),
                Resolution = ReadQuality(record.GetClassRecord($"<{nameof(DownloadBase.Resolution)}>k__BackingField")),
                AudioCodec = ReadQuality(record.GetClassRecord($"<{nameof(DownloadBase.AudioCodec)}>k__BackingField")),
                FilePath = ReadString(record, nameof(DownloadBase.FilePath)),
                FileSize = ReadString(record, nameof(DownloadBase.FileSize)),
                Page = record.GetInt32($"<{nameof(DownloadBase.Page)}>k__BackingField")
            }
        };
    }

    private static Quality ReadQuality(ClassRecord? record)
    {
        return record == null
            ? new Quality()
            : new Quality
            {
                Id = record.GetInt32($"<{nameof(Quality.Id)}>k__BackingField"),
                Name = record.GetString($"<{nameof(Quality.Name)}>k__BackingField") ?? string.Empty
            };
    }

    private static string ReadString(ClassRecord record, string propertyName)
    {
        return record.GetString($"<{propertyName}>k__BackingField") ?? string.Empty;
    }

    private string BackupMigratedDatabase(SqliteDatabase database, string databasePath)
    {
        var backupDirectory = Path.Combine(Path.GetDirectoryName(databasePath) ?? ".", "Backup");
        Directory.CreateDirectory(backupDirectory);
        var backupPath = Path.Combine(
            backupDirectory,
            $"{Path.GetFileNameWithoutExtension(databasePath)}_migrated_{Guid.NewGuid():N}{Path.GetExtension(databasePath)}");
        database.CreateBackup(backupPath);
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformationMessage($"Legacy database snapshot preserved as {Path.GetFileName(backupPath)}.");
        }
        return backupPath;
    }

    internal static bool IsMigrationException(Exception exception)
    {
        return exception is SqliteException or IOException or UnauthorizedAccessException
            or InvalidOperationException || IsLegacyRecordException(exception);
    }

    private static bool IsLegacyRecordException(Exception exception)
    {
        return exception is InvalidDataException or InvalidCastException or FormatException
            or ArgumentException or KeyNotFoundException or NotSupportedException
            or System.Runtime.Serialization.SerializationException;
    }

    private sealed record DownloadedWithData(Downloaded Downloaded, byte[] DownloadBaseData);
}
