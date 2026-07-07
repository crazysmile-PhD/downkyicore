using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.Logging;
using DownKyi.Core.Storage;
using DownKyi.Models;
using DownKyi.ViewModels.DownloadManager;
using Microsoft.Data.Sqlite;
using Console = DownKyi.Core.Utils.Debugging.Console;

namespace DownKyi.Services.Download;

/// <summary>
/// 使用原生 SQLite（Microsoft.Data.Sqlite）替代 FreeSql 的下载存储服务。
/// 单例生命周期，内部维持长连接，读写操作通过 lock 保证线程安全。
/// </summary>
public class DownloadStorageService : IDisposable
{
    private const string Tag = "DownloadStorageService";
    private readonly SqliteConnection _connection;
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public DownloadStorageService()
    {
        var dbPath = StorageManager.GetDbPath();
        var connString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();

        _connection = new SqliteConnection(connString);
        _connection.Open();

        EnsureSchema();
    }

    /// <summary>
    /// 建表（幂等），并开启外键支持
    /// </summary>
    private void EnsureSchema()
    {
        const string ddl = @"
                           PRAGMA foreign_keys = ON;

                           CREATE TABLE IF NOT EXISTS download_base (
                               id                    TEXT PRIMARY KEY,
                               need_download_content TEXT NOT NULL DEFAULT '{}',
                               bvid                  TEXT NOT NULL DEFAULT '',
                               avid                  INTEGER NOT NULL DEFAULT 0,
                               cid                   INTEGER NOT NULL DEFAULT 0,
                               episode_id            INTEGER NOT NULL DEFAULT 0,
                               cover_url             TEXT NOT NULL DEFAULT '',
                               page_cover_url        TEXT NOT NULL DEFAULT '',
                               zone_id               INTEGER NOT NULL DEFAULT 0,
                               [order]               INTEGER NOT NULL DEFAULT 0,
                               main_title            TEXT NOT NULL DEFAULT '',
                               name                  TEXT NOT NULL DEFAULT '',
                               duration              TEXT NOT NULL DEFAULT '',
                               video_codec_name      TEXT NOT NULL DEFAULT '',
                               resolution            TEXT NOT NULL DEFAULT '{}',
                               audio_codec           TEXT,
                               file_path             TEXT NOT NULL DEFAULT '',
                               file_size             TEXT,
                               page                  INTEGER NOT NULL DEFAULT 1
                           );

                           CREATE TABLE IF NOT EXISTS downloading (
                               id                    TEXT PRIMARY KEY REFERENCES download_base(id) ON DELETE CASCADE,
                               gid                   TEXT,
                               download_files        TEXT NOT NULL DEFAULT '{}',
                               downloaded_files      TEXT NOT NULL DEFAULT '[]',
                               play_stream_type      INTEGER NOT NULL DEFAULT 0,
                               download_status       INTEGER NOT NULL DEFAULT 0,
                               download_content      TEXT,
                               download_status_title TEXT,
                               progress              REAL NOT NULL DEFAULT 0,
                               downloading_file_size TEXT,
                               max_speed             INTEGER NOT NULL DEFAULT 0,
                               speed_display         TEXT
                           );

                           CREATE TABLE IF NOT EXISTS downloaded (
                               id                    TEXT PRIMARY KEY REFERENCES download_base(id) ON DELETE CASCADE,
                               max_speed_display     TEXT,
                               finished_timestamp    INTEGER NOT NULL DEFAULT 0,
                               finished_time         TEXT NOT NULL DEFAULT ''
                           );
                           ";
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = ddl;
            cmd.ExecuteNonQuery();
        }
    }

    // ─── 辅助：JSON 序列化 / 反序列化 ────────────────────────────────────────

    private static string ToJson<T>(T value) =>
        JsonSerializer.Serialize(value, JsonOptions);

    private static T FromJson<T>(string? json, T fallback) where T : new()
    {
        if (string.IsNullOrWhiteSpace(json)) return fallback;
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    // ─── DownloadBase 映射 ────────────────────────────────────────────────────

    private static DownloadBase ReadDownloadBase(SqliteDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        NeedDownloadContent = FromJson(
            r.IsDBNull(r.GetOrdinal("need_download_content"))
                ? null
                : r.GetString(r.GetOrdinal("need_download_content")),
            new Dictionary<string, bool>()),
        Bvid = r.IsDBNull(r.GetOrdinal("bvid")) ? "" : r.GetString(r.GetOrdinal("bvid")),
        Avid = r.GetInt64(r.GetOrdinal("avid")),
        Cid = r.GetInt64(r.GetOrdinal("cid")),
        EpisodeId = r.GetInt64(r.GetOrdinal("episode_id")),
        CoverUrl = r.IsDBNull(r.GetOrdinal("cover_url")) ? "" : r.GetString(r.GetOrdinal("cover_url")),
        PageCoverUrl = r.IsDBNull(r.GetOrdinal("page_cover_url")) ? "" : r.GetString(r.GetOrdinal("page_cover_url")),
        ZoneId = r.GetInt32(r.GetOrdinal("zone_id")),
        Order = r.GetInt32(r.GetOrdinal("order")),
        MainTitle = r.IsDBNull(r.GetOrdinal("main_title")) ? "" : r.GetString(r.GetOrdinal("main_title")),
        Name = r.IsDBNull(r.GetOrdinal("name")) ? "" : r.GetString(r.GetOrdinal("name")),
        Duration = r.IsDBNull(r.GetOrdinal("duration")) ? "" : r.GetString(r.GetOrdinal("duration")),
        VideoCodecName = r.IsDBNull(r.GetOrdinal("video_codec_name"))
            ? ""
            : r.GetString(r.GetOrdinal("video_codec_name")),
        Resolution = FromJson(
            r.IsDBNull(r.GetOrdinal("resolution")) ? null : r.GetString(r.GetOrdinal("resolution")),
            new Quality()),
        AudioCodec = FromJson<Quality?>(
            r.IsDBNull(r.GetOrdinal("audio_codec")) ? null : r.GetString(r.GetOrdinal("audio_codec")),
            null)!,
        FilePath = r.IsDBNull(r.GetOrdinal("file_path")) ? "" : r.GetString(r.GetOrdinal("file_path")),
        FileSize = r.IsDBNull(r.GetOrdinal("file_size")) ? null : r.GetString(r.GetOrdinal("file_size")),
        Page = r.GetInt32(r.GetOrdinal("page"))
    };

    // ─── 下载中数据 ───────────────────────────────────────────────────────────

    #region 下载中数据

    /// <summary>
    /// 添加下载中数据（幂等：已存在则跳过）
    /// </summary>
    public void AddDownloading(DownloadingItem? downloadingItem)
    {
        if (downloadingItem?.DownloadBase == null) return;

        var db = downloadingItem.DownloadBase;
        var dl = downloadingItem.Downloading;
        dl.Id = db.Id;

        lock (_lock)
        {
            using var tx = _connection.BeginTransaction();
            try
            {
                InsertOrIgnoreDownloadBase(db, tx);

                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
INSERT OR IGNORE INTO downloading
    (id, gid, download_files, downloaded_files, play_stream_type, download_status,
     download_content, download_status_title, progress, downloading_file_size, max_speed, speed_display)
VALUES
    (@id, @gid, @download_files, @downloaded_files, @play_stream_type, @download_status,
     @download_content, @download_status_title, @progress, @downloading_file_size, @max_speed, @speed_display)";
                BindDownloading(cmd, dl);
                cmd.ExecuteNonQuery();

                tx.Commit();
            }
            catch (Exception e)
            {
                tx.Rollback();
                LogManager.Error(Tag, e);
                Console.PrintLine("AddDownloading发生异常: {0}", e);
            }
        }
    }

    /// <summary>
    /// 删除下载中数据
    /// </summary>
    /// <param name="downloadingItem"></param>
    /// <param name="cascadeRemove">true=连同 download_base 一起删除</param>
    public void RemoveDownloading(DownloadingItem? downloadingItem, bool cascadeRemove = false)
    {
        if (downloadingItem?.DownloadBase == null) return;
        var id = downloadingItem.DownloadBase.Id;

        lock (_lock)
        {
            try
            {
                EnableForeignKeys();
                using var cmd = _connection.CreateCommand();
                // 外键 ON DELETE CASCADE：删除 download_base 会级联删除 downloading/downloaded
                cmd.CommandText = cascadeRemove
                    ? "DELETE FROM download_base WHERE id = @id"
                    : "DELETE FROM downloading WHERE id = @id";
                cmd.Parameters.AddWithValue("@id", id);
                cmd.ExecuteNonQuery();
            }
            catch (Exception e)
            {
                LogManager.Error(Tag, e);
                Console.PrintLine("RemoveDownloading发生异常: {0}", e);
            }
        }
    }

    /// <summary>
    /// 获取所有下载中数据
    /// </summary>
    public List<DownloadingItem> GetDownloading()
    {
        var result = new List<DownloadingItem>();
        lock (_lock)
        {
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
SELECT
    dl.id, dl.gid, dl.download_files, dl.downloaded_files, dl.play_stream_type,
    dl.download_status, dl.download_content, dl.download_status_title, dl.progress,
    dl.downloading_file_size, dl.max_speed, dl.speed_display,
    db.need_download_content, db.bvid, db.avid, db.cid, db.episode_id,
    db.cover_url, db.page_cover_url, db.zone_id, db.""order"", db.main_title,
    db.name, db.duration, db.video_codec_name, db.resolution, db.audio_codec,
    db.file_path, db.file_size, db.page
FROM downloading dl
LEFT JOIN download_base db ON db.id = dl.id";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var downloadBase = ReadDownloadBase(reader);
                    var downloading = new Downloading
                    {
                        Id = reader.GetString(reader.GetOrdinal("id")),
                        Gid = reader.IsDBNull(reader.GetOrdinal("gid"))
                            ? null
                            : reader.GetString(reader.GetOrdinal("gid")),
                        DownloadFiles = FromJson(
                            reader.IsDBNull(reader.GetOrdinal("download_files"))
                                ? null
                                : reader.GetString(reader.GetOrdinal("download_files")),
                            new Dictionary<string, string>()),
                        DownloadedFiles = FromJson(
                            reader.IsDBNull(reader.GetOrdinal("downloaded_files"))
                                ? null
                                : reader.GetString(reader.GetOrdinal("downloaded_files")),
                            new List<string>()),
                        PlayStreamType = (PlayStreamType)reader.GetInt32(reader.GetOrdinal("play_stream_type")),
                        DownloadStatus = (DownloadStatus)reader.GetInt32(reader.GetOrdinal("download_status")),
                        DownloadContent = reader.IsDBNull(reader.GetOrdinal("download_content"))
                            ? null
                            : reader.GetString(reader.GetOrdinal("download_content")),
                        DownloadStatusTitle = reader.IsDBNull(reader.GetOrdinal("download_status_title"))
                            ? null
                            : reader.GetString(reader.GetOrdinal("download_status_title")),
                        Progress = reader.GetFloat(reader.GetOrdinal("progress")),
                        DownloadingFileSize = reader.IsDBNull(reader.GetOrdinal("downloading_file_size"))
                            ? null
                            : reader.GetString(reader.GetOrdinal("downloading_file_size")),
                        MaxSpeed = reader.GetInt64(reader.GetOrdinal("max_speed")),
                        SpeedDisplay = reader.IsDBNull(reader.GetOrdinal("speed_display"))
                            ? null
                            : reader.GetString(reader.GetOrdinal("speed_display")),
                        DownloadBase = downloadBase
                    };
                    result.Add(new DownloadingItem { Downloading = downloading, DownloadBase = downloadBase });
                }
            }
            catch (Exception e)
            {
                LogManager.Error(Tag, e);
                Console.PrintLine("GetDownloading发生异常: {0}", e);
            }
        }

        return result;
    }

    /// <summary>
    /// 更新下载中数据
    /// </summary>
    public void UpdateDownloading(DownloadingItem? downloadingItem)
    {
        if (downloadingItem?.DownloadBase == null) return;

        var dl = downloadingItem.Downloading;
        dl.DownloadBase = downloadingItem.DownloadBase;

        lock (_lock)
        {
            using var tx = _connection.BeginTransaction();
            try
            {
                UpdateDownloadBase(downloadingItem.DownloadBase, tx);

                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
UPDATE downloading SET
    gid = @gid,
    download_files = @download_files,
    downloaded_files = @downloaded_files,
    play_stream_type = @play_stream_type,
    download_status = @download_status,
    download_content = @download_content,
    download_status_title = @download_status_title,
    progress = @progress,
    downloading_file_size = @downloading_file_size,
    max_speed = @max_speed,
    speed_display = @speed_display
WHERE id = @id";
                BindDownloading(cmd, dl);
                cmd.ExecuteNonQuery();

                tx.Commit();
            }
            catch (Exception e)
            {
                tx.Rollback();
                LogManager.Error(Tag, e);
                Console.PrintLine("UpdateDownloading发生异常: {0}", e);
            }
        }
    }

    #endregion

    // ─── 下载完成数据 ─────────────────────────────────────────────────────────

    #region 下载完成数据

    /// <summary>
    /// 添加下载完成数据（幂等：已存在则跳过）
    /// </summary>
    public void AddDownloaded(DownloadedItem? downloadedItem)
    {
        if (downloadedItem?.DownloadBase == null) return;

        var db = downloadedItem.DownloadBase;
        var d = downloadedItem.Downloaded;
        d.Id = db.Id;

        lock (_lock)
        {
            using var tx = _connection.BeginTransaction();
            try
            {
                InsertOrIgnoreDownloadBase(db, tx);

                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
INSERT OR IGNORE INTO downloaded (id, max_speed_display, finished_timestamp, finished_time)
VALUES (@id, @max_speed_display, @finished_timestamp, @finished_time)";
                BindDownloaded(cmd, d);
                cmd.ExecuteNonQuery();

                tx.Commit();
            }
            catch (Exception e)
            {
                tx.Rollback();
                LogManager.Error(Tag, e);
                Console.PrintLine("AddDownloaded发生异常: {0}", e);
            }
        }
    }

    /// <summary>
    /// 批量插入下载完成数据（用于数据迁移）
    /// </summary>
    public void AddDownloadedBatch(IEnumerable<Downloaded> items)
    {
        lock (_lock)
        {
            using var tx = _connection.BeginTransaction();
            try
            {
                foreach (var d in items)
                {
                    if (d.DownloadBase == null) continue;
                    InsertOrIgnoreDownloadBase(d.DownloadBase, tx);

                    using var cmd = _connection.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = @"
INSERT OR IGNORE INTO downloaded (id, max_speed_display, finished_timestamp, finished_time)
VALUES (@id, @max_speed_display, @finished_timestamp, @finished_time)";
                    BindDownloaded(cmd, d);
                    cmd.ExecuteNonQuery();
                }

                tx.Commit();
            }
            catch (Exception e)
            {
                tx.Rollback();
                LogManager.Error(Tag, e);
                Console.PrintLine("AddDownloadedBatch发生异常: {0}", e);
            }
        }
    }

    /// <summary>
    /// 删除下载完成数据（级联删除 download_base）
    /// </summary>
    public void RemoveDownloaded(DownloadedItem? downloadedItem)
    {
        if (downloadedItem?.DownloadBase == null) return;
        var id = downloadedItem.DownloadBase.Id;

        lock (_lock)
        {
            try
            {
                EnableForeignKeys();
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "DELETE FROM download_base WHERE id = @id";
                cmd.Parameters.AddWithValue("@id", id);
                cmd.ExecuteNonQuery();
            }
            catch (Exception e)
            {
                LogManager.Error(Tag, e);
                Console.PrintLine("RemoveDownloaded发生异常: {0}", e);
            }
        }
    }

    /// <summary>
    /// 获取所有下载完成数据
    /// </summary>
    public List<DownloadedItem> GetDownloaded()
    {
        var result = new List<DownloadedItem>();
        lock (_lock)
        {
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
SELECT
    d.id, d.max_speed_display, d.finished_timestamp, d.finished_time,
    db.need_download_content, db.bvid, db.avid, db.cid, db.episode_id,
    db.cover_url, db.page_cover_url, db.zone_id, db.""order"", db.main_title,
    db.name, db.duration, db.video_codec_name, db.resolution, db.audio_codec,
    db.file_path, db.file_size, db.page
FROM downloaded d
LEFT JOIN download_base db ON db.id = d.id";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var downloadBase = ReadDownloadBase(reader);
                    var downloaded = new Downloaded
                    {
                        Id = reader.GetString(reader.GetOrdinal("id")),
                        MaxSpeedDisplay = reader.IsDBNull(reader.GetOrdinal("max_speed_display"))
                            ? null
                            : reader.GetString(reader.GetOrdinal("max_speed_display")),
                        FinishedTimestamp = reader.GetInt64(reader.GetOrdinal("finished_timestamp")),
                        FinishedTime = reader.IsDBNull(reader.GetOrdinal("finished_time"))
                            ? ""
                            : reader.GetString(reader.GetOrdinal("finished_time")),
                        DownloadBase = downloadBase
                    };
                    result.Add(new DownloadedItem { Downloaded = downloaded, DownloadBase = downloadBase });
                }
            }
            catch (Exception e)
            {
                LogManager.Error(Tag, e);
                Console.PrintLine("GetDownloaded发生异常: {0}", e);
            }
        }

        return result;
    }

    /// <summary>
    /// 更新下载完成数据
    /// </summary>
    public void UpdateDownloaded(DownloadedItem? downloadedItem)
    {
        if (downloadedItem?.DownloadBase == null) return;

        var d = downloadedItem.Downloaded;
        d.DownloadBase = downloadedItem.DownloadBase;

        lock (_lock)
        {
            using var tx = _connection.BeginTransaction();
            try
            {
                UpdateDownloadBase(downloadedItem.DownloadBase, tx);

                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
UPDATE downloaded SET
    max_speed_display = @max_speed_display,
    finished_timestamp = @finished_timestamp,
    finished_time = @finished_time
WHERE id = @id";
                BindDownloaded(cmd, d);
                cmd.ExecuteNonQuery();

                tx.Commit();
            }
            catch (Exception e)
            {
                tx.Rollback();
                LogManager.Error(Tag, e);
                Console.PrintLine("UpdateDownloaded发生异常: {0}", e);
            }
        }
    }

    /// <summary>
    /// 清空所有下载完成记录（同时清空对应的 download_base 记录）
    /// </summary>
    public void ClearDownloaded()
    {
        lock (_lock)
        {
            using var tx = _connection.BeginTransaction();
            try
            {
                EnableForeignKeys();
                // 只删 downloaded 中存在的 download_base（不影响 downloading 中还在用的）
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
DELETE FROM download_base
WHERE id IN (SELECT id FROM downloaded)
  AND id NOT IN (SELECT id FROM downloading)";
                cmd.ExecuteNonQuery();

                // 剩余（同时在 downloading 中）只删 downloaded 记录
                using var cmd2 = _connection.CreateCommand();
                cmd2.Transaction = tx;
                cmd2.CommandText = "DELETE FROM downloaded";
                cmd2.ExecuteNonQuery();

                tx.Commit();
            }
            catch (Exception e)
            {
                tx.Rollback();
                LogManager.Error(Tag, e);
                Console.PrintLine("ClearDownloaded发生异常: {0}", e);
            }
        }
    }

    #endregion

    // ─── 私有辅助方法 ─────────────────────────────────────────────────────────

    private void EnableForeignKeys()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON";
        cmd.ExecuteNonQuery();
    }

    private void InsertOrIgnoreDownloadBase(DownloadBase db, SqliteTransaction tx)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
INSERT OR IGNORE INTO download_base
    (id, need_download_content, bvid, avid, cid, episode_id, cover_url, page_cover_url,
     zone_id, ""order"", main_title, name, duration, video_codec_name, resolution,
     audio_codec, file_path, file_size, page)
VALUES
    (@id, @need_download_content, @bvid, @avid, @cid, @episode_id, @cover_url, @page_cover_url,
     @zone_id, @order, @main_title, @name, @duration, @video_codec_name, @resolution,
     @audio_codec, @file_path, @file_size, @page)";
        BindDownloadBase(cmd, db);
        cmd.ExecuteNonQuery();
    }

    private void UpdateDownloadBase(DownloadBase db, SqliteTransaction tx)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
UPDATE download_base SET
    need_download_content = @need_download_content,
    bvid = @bvid, avid = @avid, cid = @cid, episode_id = @episode_id,
    cover_url = @cover_url, page_cover_url = @page_cover_url,
    zone_id = @zone_id, ""order"" = @order, main_title = @main_title, name = @name,
    duration = @duration, video_codec_name = @video_codec_name, resolution = @resolution,
    audio_codec = @audio_codec, file_path = @file_path, file_size = @file_size, page = @page
WHERE id = @id";
        BindDownloadBase(cmd, db);
        cmd.ExecuteNonQuery();
    }

    private static void BindDownloadBase(SqliteCommand cmd, DownloadBase db)
    {
        cmd.Parameters.AddWithValue("@id", db.Id);
        cmd.Parameters.AddWithValue("@need_download_content", ToJson(db.NeedDownloadContent));
        cmd.Parameters.AddWithValue("@bvid", db.Bvid);
        cmd.Parameters.AddWithValue("@avid", db.Avid);
        cmd.Parameters.AddWithValue("@cid", db.Cid);
        cmd.Parameters.AddWithValue("@episode_id", db.EpisodeId);
        cmd.Parameters.AddWithValue("@cover_url", db.CoverUrl);
        cmd.Parameters.AddWithValue("@page_cover_url", db.PageCoverUrl);
        cmd.Parameters.AddWithValue("@zone_id", db.ZoneId);
        cmd.Parameters.AddWithValue("@order", db.Order);
        cmd.Parameters.AddWithValue("@main_title", db.MainTitle);
        cmd.Parameters.AddWithValue("@name", db.Name);
        cmd.Parameters.AddWithValue("@duration", db.Duration);
        cmd.Parameters.AddWithValue("@video_codec_name", db.VideoCodecName);
        cmd.Parameters.AddWithValue("@resolution", ToJson(db.Resolution));
        cmd.Parameters.AddWithValue("@audio_codec", ToJson(db.AudioCodec));
        cmd.Parameters.AddWithValue("@file_path", db.FilePath);
        cmd.Parameters.AddWithValue("@file_size", db.FileSize ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@page", db.Page);
    }

    private static void BindDownloading(SqliteCommand cmd, Downloading dl)
    {
        cmd.Parameters.AddWithValue("@id", dl.Id);
        cmd.Parameters.AddWithValue("@gid", dl.Gid ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@download_files", ToJson(dl.DownloadFiles));
        cmd.Parameters.AddWithValue("@downloaded_files", ToJson(dl.DownloadedFiles));
        cmd.Parameters.AddWithValue("@play_stream_type", (int)dl.PlayStreamType);
        cmd.Parameters.AddWithValue("@download_status", (int)dl.DownloadStatus);
        cmd.Parameters.AddWithValue("@download_content", dl.DownloadContent ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@download_status_title", dl.DownloadStatusTitle ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@progress", dl.Progress);
        cmd.Parameters.AddWithValue("@downloading_file_size", dl.DownloadingFileSize ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@max_speed", dl.MaxSpeed);
        cmd.Parameters.AddWithValue("@speed_display", dl.SpeedDisplay ?? (object)DBNull.Value);
    }

    private static void BindDownloaded(SqliteCommand cmd, Downloaded d)
    {
        cmd.Parameters.AddWithValue("@id", d.Id);
        cmd.Parameters.AddWithValue("@max_speed_display", d.MaxSpeedDisplay ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@finished_timestamp", d.FinishedTimestamp);
        cmd.Parameters.AddWithValue("@finished_time", d.FinishedTime);
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }
}