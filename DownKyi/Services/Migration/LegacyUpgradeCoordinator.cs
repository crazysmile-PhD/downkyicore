using System;
using System.Collections.Generic;
using System.Formats.Nrbf;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.Login;
using DownKyi.Core.Logging;
using DownKyi.Core.Storage;
using DownKyi.Core.Storage.Database;
using DownKyi.Models;
using DownKyi.Services.Download;
using DownKyi.ViewModels.DownloadManager;
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
    IReadOnlyList<DownloadedItem> DownloadedItems,
    string? ErrorMessage = null);

internal interface ILegacyUpgradeCoordinator
{
    Task<LegacyUpgradeResult> UpgradeAsync(
        IProgress<LegacyUpgradeProgress> progress,
        CancellationToken cancellationToken);
}

internal sealed class LegacyUpgradeCoordinator : ILegacyUpgradeCoordinator
{
    private const int BatchSize = 200;
    private readonly DownloadStorageService _downloadStorageService;
    private readonly ILogger<LegacyUpgradeCoordinator> _logger;

    public LegacyUpgradeCoordinator(
        DownloadStorageService downloadStorageService,
        ILogger<LegacyUpgradeCoordinator> logger)
    {
        _downloadStorageService = downloadStorageService
            ?? throw new ArgumentNullException(nameof(downloadStorageService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
            return new LegacyUpgradeResult(LegacyUpgradeOutcome.NoMigration, Array.Empty<DownloadedItem>());
        }

        progress.Report(new LegacyUpgradeProgress("正在迁移下载信息"));
        try
        {
            var records = ReadLegacyDatabase(oldDatabasePath, progress, cancellationToken);
            if (records == null)
            {
                var backupMessage = BackupFailedDatabase(oldDatabasePath);
                return new LegacyUpgradeResult(
                    LegacyUpgradeOutcome.Failed,
                    Array.Empty<DownloadedItem>(),
                    backupMessage);
            }

            await PersistRecordsAsync(records, progress, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(oldDatabasePath);

            var downloadedItems = await _downloadStorageService
                .GetDownloadedAsync(cancellationToken)
                .ConfigureAwait(false);
            progress.Report(new LegacyUpgradeProgress("下载信息迁移完成", 100));
            return new LegacyUpgradeResult(LegacyUpgradeOutcome.Completed, downloadedItems);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (IsMigrationException(e))
        {
            _logger.LogErrorMessage("Legacy upgrade failed.", e);
            var backupMessage = File.Exists(oldDatabasePath)
                ? BackupFailedDatabase(oldDatabasePath)
                : null;
            var message = backupMessage == null
                ? $"数据迁移失败: {e.Message}"
                : $"数据迁移失败: {e.Message}; {backupMessage}";
            return new LegacyUpgradeResult(
                LegacyUpgradeOutcome.Failed,
                Array.Empty<DownloadedItem>(),
                message);
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
            progress.Report(new LegacyUpgradeProgress("登录信息迁移失败，继续迁移下载信息"));
        }
    }

    private static void MigrateLogin(
        IProgress<LegacyUpgradeProgress> progress,
        CancellationToken cancellationToken)
    {
        var loginPath = StorageManager.GetLogin();
        if (!File.Exists(loginPath))
        {
            return;
        }

        using Stream stream = File.Open(loginPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!NrbfDecoder.StartsWithPayloadHeader(stream))
        {
            return;
        }

        progress.Report(new LegacyUpgradeProgress("正在迁移登录信息"));
        var cookies = new List<DownKyiCookie>();
        var cookieRecord = NrbfDecoder.DecodeClassRecord(stream);
        if (cookieRecord.TypeNameMatches(typeof(CookieContainer)))
        {
            var values = cookieRecord
                .GetClassRecord("m_domainTable")?
                .GetArrayRecord("Values")?
                .GetArray(typeof(object[]))
                .Cast<ClassRecord>();
            foreach (var value in values ?? Array.Empty<ClassRecord>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var valueObjects = value
                    .GetClassRecord("m_list")?
                    .GetClassRecord("_list")?
                    .GetArrayRecord("values")?
                    .GetArray(typeof(object[]));
                foreach (var valueObject in valueObjects?.Cast<ClassRecord>() ?? Array.Empty<ClassRecord>())
                {
                    var records = valueObject
                        .GetClassRecord("m_list")?
                        .GetArrayRecord("_items")?
                        .GetArray(typeof(object[]))
                        .Cast<ClassRecord>();
                    foreach (var cookie in records ?? Array.Empty<ClassRecord>())
                    {
                        cookies.Add(new DownKyiCookie(
                            cookie.GetString("m_name") ?? string.Empty,
                            cookie.GetString("m_value") ?? string.Empty,
                            cookie.GetString("m_domain")));
                    }
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!LoginHelper.SaveLoginInfoCookies(cookies))
        {
            throw new IOException("Legacy login cookies could not be persisted.");
        }
        progress.Report(new LegacyUpgradeProgress("登录信息迁移完成"));
    }

    private static string? FindLegacyDatabase()
    {
        var downloadPath = StorageManager.GetDownload();
        var possiblePaths = new[]
        {
            downloadPath,
            downloadPath.Replace(".db", "_debug.db", StringComparison.Ordinal)
        };
        return possiblePaths.FirstOrDefault(File.Exists);
    }

    private Dictionary<string, DownloadedWithData>? ReadLegacyDatabase(
        string databasePath,
        IProgress<LegacyUpgradeProgress> progress,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var database = attempt == 1
                    ? new SqliteDatabase(databasePath, "bdb8eb69-3698-4af9-b722-9312d0fba623")
                    : new SqliteDatabase(databasePath);
                if (attempt > 1)
                {
                    progress.Report(new LegacyUpgradeProgress("尝试备用连接方式"));
                }

                ValidateLegacyDatabase(database);
                return ReadLegacyRecords(database, progress, cancellationToken);
            }
            catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException
                or InvalidOperationException or ArgumentException)
            {
                _logger.LogErrorMessage($"Legacy database connection attempt {attempt} failed.", e);
                progress.Report(new LegacyUpgradeProgress($"数据库连接尝试 {attempt} 失败"));
            }
        }

        progress.Report(new LegacyUpgradeProgress("数据库连接尝试次数超限，放弃迁移"));
        return null;
    }

    private static void ValidateLegacyDatabase(SqliteDatabase database)
    {
        var tableCount = 0;
        database.ExecuteQuery(
            command => command.CommandText = "SELECT name FROM sqlite_master WHERE type='table'",
            reader =>
            {
                while (reader.Read())
                {
                    tableCount++;
                }
            });
        if (tableCount < 2)
        {
            throw new SqliteException("数据库表不存在或结构不完整", 1);
        }

        var hasRequiredTables = false;
        database.ExecuteQuery(
            command => command.CommandText = """
                SELECT COUNT(*)
                FROM sqlite_master
                WHERE type='table' AND name IN ('downloaded', 'download_base')
                """,
            reader =>
            {
                if (reader.Read())
                {
                    hasRequiredTables = reader.GetInt32(0) == 2;
                }
            });
        if (!hasRequiredTables)
        {
            throw new SqliteException("缺少必要的数据库表", 1);
        }
    }

    private Dictionary<string, DownloadedWithData> ReadLegacyRecords(
        SqliteDatabase database,
        IProgress<LegacyUpgradeProgress> progress,
        CancellationToken cancellationToken)
    {
        var records = new Dictionary<string, DownloadedWithData>(StringComparer.Ordinal);
        database.ExecuteQuery(
            command => command.CommandText = """
                SELECT d.id, d.data AS downloaded_data, db.data AS download_base_data
                FROM downloaded d
                JOIN download_base db ON d.id = db.id
                """,
            reader =>
            {
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        using var stream = new MemoryStream((byte[])reader["downloaded_data"]);
                        var record = NrbfDecoder.DecodeClassRecord(stream);
                        if (!record.TypeNameMatches(typeof(Downloaded)))
                        {
                            continue;
                        }

                        var id = reader["id"].ToString() ?? Guid.NewGuid().ToString("N");
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
                    catch (Exception e) when (IsLegacyRecordException(e))
                    {
                        _logger.LogErrorMessage("A damaged legacy download record was skipped.", e);
                        progress.Report(new LegacyUpgradeProgress("已跳过一笔损坏的旧下载记录"));
                    }
                }
            });
        return records;
    }

    private async Task PersistRecordsAsync(
        Dictionary<string, DownloadedWithData> records,
        IProgress<LegacyUpgradeProgress> progress,
        CancellationToken cancellationToken)
    {
        var batch = new List<Downloaded>(Math.Min(BatchSize, records.Count));
        var visited = 0;
        foreach (var item in records.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            visited++;
            try
            {
                var downloaded = ConvertDownload(item);
                if (downloaded != null)
                {
                    batch.Add(downloaded);
                }
            }
            catch (Exception e) when (IsLegacyRecordException(e) || e is SqliteException)
            {
                _logger.LogErrorMessage("A damaged legacy download detail was skipped.", e);
                progress.Report(new LegacyUpgradeProgress("已跳过一笔损坏的旧下载详情"));
            }

            if (batch.Count >= BatchSize)
            {
                await _downloadStorageService
                    .AddDownloadedBatchAsync(batch, cancellationToken)
                    .ConfigureAwait(false);
                batch.Clear();
            }

            var percent = records.Count == 0 ? 100 : visited / (double)records.Count * 100;
            progress.Report(new LegacyUpgradeProgress(
                $"正在迁移下载信息({visited}/{records.Count})",
                percent));
        }

        if (batch.Count > 0)
        {
            await _downloadStorageService
                .AddDownloadedBatchAsync(batch, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static Downloaded? ConvertDownload(DownloadedWithData item)
    {
        using var stream = new MemoryStream(item.DownloadBaseData);
        var record = NrbfDecoder.DecodeClassRecord(stream);
        if (!record.TypeNameMatches(typeof(DownloadBase)))
        {
            return null;
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
                NeedDownloadContent = needDownloadContentRecord == null
                    ? new Dictionary<string, bool>()
                    : ReadNeedDownloadContent(needDownloadContentRecord),
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

    private static Dictionary<string, bool> ReadNeedDownloadContent(ClassRecord record)
    {
        var values = record
            .GetArrayRecord("KeyValuePairs")?
            .GetArray(typeof(KeyValuePair<string, bool>[]));
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var item in values?.Cast<ClassRecord>() ?? Array.Empty<ClassRecord>())
        {
            result[item.GetString("key") ?? string.Empty] = item.GetBoolean("value");
        }

        return result;
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

    private string BackupFailedDatabase(string databasePath)
    {
        try
        {
            var backupDirectory = Path.Combine(Path.GetDirectoryName(databasePath) ?? ".", "Backup");
            Directory.CreateDirectory(backupDirectory);
            var backupPath = Path.Combine(
                backupDirectory,
                $"{Path.GetFileNameWithoutExtension(databasePath)}_failed_{DateTime.Now:yyyyMMdd_HHmmss_fff}{Path.GetExtension(databasePath)}");
            File.Move(databasePath, backupPath);
            return $"原数据库已备份为 {Path.GetFileName(backupPath)}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogErrorMessage("Legacy database backup failed; trying a rename fallback.", e);
            try
            {
                var renamedPath = $"{databasePath}.corrupted_{DateTime.Now:yyyyMMdd_HHmmss_fff}";
                File.Move(databasePath, renamedPath);
                return $"原数据库已重命名为 {Path.GetFileName(renamedPath)}";
            }
            catch (Exception fallback) when (fallback is IOException or UnauthorizedAccessException)
            {
                _logger.LogErrorMessage("Legacy database rename fallback failed.", fallback);
                return "无法自动备份旧数据库，请保留日志并手动处理";
            }
        }
    }

    private static bool IsMigrationException(Exception exception)
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
